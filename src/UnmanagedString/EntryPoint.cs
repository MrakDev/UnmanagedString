using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Code.Native;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using AsmResolver.PE.File.Headers;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;
using ModuleDefinition = AsmResolver.DotNet.ModuleDefinition;

namespace UnmanagedString;

public static class EntryPoint
{
    public static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            Logger.Error("Usage: UnmanagedString.exe <path to assembly>");
            return;
        }
        
        if (!File.Exists(args[0]))
        {
            Logger.Error($"File not found: {args[0]}");
            return;
        }

        var file = File.ReadAllBytes(args[0]);
        var module = ModuleDefinition.FromBytes(file);
        var importer = new ReferenceImporter(module);
        
        Logger.Information("Starting...");

        module.Attributes &= ~DotNetDirectoryFlags.ILOnly;
        var isx86 = module.MachineType == MachineType.I386;

        if (isx86)
        {
            module.PEKind = OptionalHeaderMagic.Pe32;
            module.MachineType = MachineType.I386;
            module.Attributes |= DotNetDirectoryFlags.Bit32Required;
        }
        else
        {
            module.PEKind = OptionalHeaderMagic.Pe32Plus;
            module.MachineType = MachineType.Amd64;
        }

        foreach (var type in module.GetAllTypes())
        foreach (var method in type.Methods)
            for (var index = 0; index < method.CilMethodBody!.Instructions.Count; ++index)
            {
                var instruction = method.CilMethodBody!.Instructions[index];

                if (instruction.OpCode != CilOpCodes.Ldstr)
                    continue;

                var newNativeMethod =
                    CreateNewNativeMethodWithString(
                        instruction.Operand as string ?? throw new InvalidCilInstructionException(), module, isx86);

                instruction.OpCode = CilOpCodes.Call;
                instruction.Operand = newNativeMethod;

                method.CilMethodBody.Instructions.Insert(++index,
                    new CilInstruction(CilOpCodes.Newobj,
                        importer.ImportMethod(
                            typeof(string).GetConstructor(new[] {typeof(sbyte*)})!)));
            }
        
        module.Write(args[0] + "_strings.exe");
        Logger.Success("Done!");
    }

    private static MethodDefinition CreateNewNativeMethodWithString(string toInject, ModuleDefinition originalModule,
        bool isX86)
    {
        if (toInject == null || originalModule == null)
            throw new ArgumentNullException(nameof(toInject));
        
        if (originalModule == null)
            throw new ArgumentNullException(nameof(originalModule));

        var factory = originalModule.CorLibTypeFactory;

        // Create new method with public and static visibility.
        var methodName = Guid.NewGuid().ToString();
        var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(factory.SByte.MakePointerType()));

        // Set ImplAttributes to NativeBody.
        method.ImplAttributes |= MethodImplAttributes.Native | MethodImplAttributes.Unmanaged |
                                 MethodImplAttributes.PreserveSig;
        
        // Set Attributes to PinvokeImpl.
        method.Attributes |= MethodAttributes.PInvokeImpl;

        originalModule.GetOrCreateModuleType().Methods.Add(method);

        // Register new encoding provider. 
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var w1252Encoding = Encoding.GetEncoding(1252); // Windows-1252

        // Get the string bytes.
        var stringBytes = w1252Encoding.GetBytes(toInject);

        // Create a new NativeMethodBody with x64 or x32 byte code.
        NativeMethodBody body;

        if (isX86)
            body = new NativeMethodBody(method)
            {
                Code = new byte[]
                {
                    0x55, // push ebp
                    0x89, 0xE5, // mov ebp, esp
                    0xE8, 0x05, 0x00, 0x00, 0x00, // call <jump1>
                    0x83, 0xC0, 0x01, // add eax, 1
                    // <jump2>:
                    0x5D, // pop ebp
                    0xC3, // ret
                    // <jump1>:
                    0x58, // pop eax
                    0x83, 0xC0, 0x0B, // add eax, 0xb
                    0xEB, 0xF8 // jmp <jump2>
                }.Concat(stringBytes).Concat(new byte[] {0x00}).ToArray()
            };
        else
            body = new NativeMethodBody(method)
            {
                Code = new byte[]
                {
                    0x48, 0x8D, 0x05, 0x01, 0x00, 0x00, 0x00, // lea rax, [rip + 0x1]
                    0xC3 // ret
                }.Concat(stringBytes).Concat(new byte[] {0x00}).ToArray()
            };
        
        Logger.Success($"Created new native method with name: {methodName} for string: {toInject}");
        method.NativeMethodBody = body;
        return method;
    }
}