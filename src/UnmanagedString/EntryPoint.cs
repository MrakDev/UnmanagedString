using System.Text;
using AsmResolver.DotNet;
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

        var module = ModuleDefinition.FromFile(args[0]);
        var importer = new ReferenceImporter(module);

        var stringSbytePointerCtor =
            importer.ImportMethod(typeof(string).GetConstructor(new[] { typeof(sbyte*) })!);
        var stringCharPointerCtor =
            importer.ImportMethod(typeof(string).GetConstructor(new[] { typeof(char*) })!);
        var stringSbytePointerWithLengthCtor =
            importer.ImportMethod(typeof(string).GetConstructor(new[] { typeof(sbyte*), typeof(int), typeof(int) })!);
        var stringCharPointerWithLengthCtor =
            importer.ImportMethod(typeof(string).GetConstructor(new[] { typeof(char*), typeof(int), typeof(int) })!);

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

        var encodedStrings = new Dictionary<string, MethodDefinition>();

        foreach (var type in module.GetAllTypes())
        foreach (var method in type.Methods)
            for (var index = 0; index < method.CilMethodBody!.Instructions.Count; ++index)
            {
                var instruction = method.CilMethodBody!.Instructions[index];

                if (instruction.OpCode == CilOpCodes.Ldstr &&
                    instruction.Operand is string { Length: > 0 } content)
                {
                    var useUnicode = !CanBeEncodedIn7BitAscii(content);
                    var addNullTerminator = !HasNullCharacter(content);

                    if (!encodedStrings.TryGetValue(content, out var nativeMethod)) // reuse encoded strings
                    {
                        nativeMethod = CreateNewNativeMethodWithString(content, module, isx86, useUnicode,
                            addNullTerminator);
                        encodedStrings.Add(content, nativeMethod);
                    }

                    instruction.ReplaceWith(CilOpCodes.Call, nativeMethod);
                    if (addNullTerminator)
                    {
                        method.CilMethodBody.Instructions.Insert(++index,
                            new CilInstruction(CilOpCodes.Newobj,
                                useUnicode ? stringCharPointerCtor : stringSbytePointerCtor));
                    }
                    else
                    {
                        method.CilMethodBody.Instructions.Insert(++index,
                            CilInstruction.CreateLdcI4(0));
                        method.CilMethodBody.Instructions.Insert(++index,
                            CilInstruction.CreateLdcI4(content.Length));
                        method.CilMethodBody.Instructions.Insert(++index,
                            new CilInstruction(CilOpCodes.Newobj,
                                useUnicode ? stringCharPointerWithLengthCtor : stringSbytePointerWithLengthCtor));
                    }
                }
            }

        module.Write(args[0] + "_strings.exe");
        Logger.Success("Done!");
    }

    private static MethodDefinition? CreateNewNativeMethodWithString(string content, ModuleDefinition originalModule,
        bool isX86, bool useUnicode, bool addNullTerminator)
    {
        ArgumentNullException.ThrowIfNull(originalModule);
        ArgumentNullException.ThrowIfNull(content);

        var factory = originalModule.CorLibTypeFactory;

        var methodName = Guid.NewGuid().ToString();
        var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(factory.SByte.MakePointerType()));

        method.ImplAttributes |= MethodImplAttributes.Native | MethodImplAttributes.Unmanaged |
                                 MethodImplAttributes.PreserveSig;
        method.Attributes |= MethodAttributes.PInvokeImpl;

        originalModule.GetOrCreateModuleType().Methods.Add(method);

        if (addNullTerminator)
        {
            content += "\0"; // not adding on byte level as it has encoding-dependent size
        }

        var stringBytes = useUnicode
            ? Encoding.Unicode.GetBytes(content)
            : Encoding.ASCII.GetBytes(content);

        var prefix = isX86
            ? stackalloc byte[]
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
            }
            : stackalloc byte[]
            {
                0x48, 0x8D, 0x05, 0x01, 0x00, 0x00, 0x00, // lea rax, [rip + 0x1]
                0xC3 // ret
            };

        Span<byte> code = stackalloc byte[prefix.Length + stringBytes.Length];
        prefix.CopyTo(code);
        stringBytes.CopyTo(code[prefix.Length..]);

        var body = new NativeMethodBody(method)
        {
            Code = code.ToArray()
        };

        Logger.Success($"Created new native method with name: {methodName} for string: {content.TrimEnd()}");
        method.NativeMethodBody = body;
        return method;
    }

    private static bool CanBeEncodedIn7BitAscii(ReadOnlySpan<char> text)
    {
        // ReSharper disable once ForCanBeConvertedToForeach
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > '\x7f')
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNullCharacter(ReadOnlySpan<char> text)
    {
        // ReSharper disable once ForCanBeConvertedToForeach
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\0')
            {
                return true;
            }
        }

        return false;
    }
}