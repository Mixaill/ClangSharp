// Copyright (c) Microsoft and Contributors. All rights reserved. Licensed under the University of Illinois/NCSA Open Source License. See LICENSE.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ClangSharp.Interop;

namespace ClangSharp
{
    public sealed partial class PInvokeGenerator : IDisposable
    {
        private const int DefaultStreamWriterBufferSize = 1024;
        private static readonly Encoding defaultStreamWriterEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly CXIndex _index;
        private readonly OutputBuilderFactory _outputBuilderFactory;
        private readonly Func<string, Stream> _outputStreamFactory;
        private readonly HashSet<Cursor> _visitedCursors;
        private readonly List<Diagnostic> _diagnostics;
        private readonly PInvokeGeneratorConfiguration _config;

        private OutputBuilder _outputBuilder;
        private int _outputBuilderUsers;
        private bool _disposed;
        private bool _isMethodClassUnsafe;

        public PInvokeGenerator(PInvokeGeneratorConfiguration config, Func<string, Stream> outputStreamFactory = null)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _index = CXIndex.Create();
            _outputBuilderFactory = new OutputBuilderFactory();
            _outputStreamFactory = outputStreamFactory ?? ((path) => {
                var directoryPath = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directoryPath);
                return new FileStream(path, FileMode.Create);
            });
            _visitedCursors = new HashSet<Cursor>();
            _diagnostics = new List<Diagnostic>();
            _config = config;
        }

        ~PInvokeGenerator()
        {
            Dispose(isDisposing: false);
        }

        public PInvokeGeneratorConfiguration Config => _config;

        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        public CXIndex IndexHandle => _index;

        public void Close()
        {
            Stream stream = null;
            OutputBuilder methodClassOutputBuilder = null;
            bool emitNamespaceDeclaration = true;
            bool leaveStreamOpen = false;

            if (!_config.GenerateMultipleFiles)
            {
                var outputPath = _config.OutputLocation;
                stream = _outputStreamFactory(outputPath);
                leaveStreamOpen = true;

                var usingDirectives = Enumerable.Empty<string>();

                foreach (var outputBuilder in _outputBuilderFactory.OutputBuilders)
                {
                    usingDirectives = usingDirectives.Concat(outputBuilder.UsingDirectives);
                }

                usingDirectives = usingDirectives.Distinct()
                                                 .OrderBy((usingDirective) => usingDirective);

                if (usingDirectives.Any())
                {
                    using var sw = new StreamWriter(stream, defaultStreamWriterEncoding, DefaultStreamWriterBufferSize, leaveStreamOpen);
                    {
                        sw.NewLine = "\n";
                        sw.Write(_config.HeaderText);

                        foreach (var usingDirective in usingDirectives)
                        {
                            sw.Write("using");
                            sw.Write(' ');
                            sw.Write(usingDirective);
                            sw.WriteLine(';');
                        }

                        sw.WriteLine();
                    }
                }
            }

            foreach (var outputBuilder in _outputBuilderFactory.OutputBuilders)
            {
                var outputPath = _config.OutputLocation;
                var isMethodClass = _config.MethodClassName.Equals(outputBuilder.Name);

                if (_config.GenerateMultipleFiles)
                {
                    outputPath = Path.Combine(outputPath, $"{outputBuilder.Name}.cs");
                    stream = _outputStreamFactory(outputPath);
                    emitNamespaceDeclaration = true;
                }
                else if (isMethodClass)
                {
                    methodClassOutputBuilder = outputBuilder;
                    continue;
                }

                CloseOutputBuilder(stream, outputBuilder, isMethodClass, leaveStreamOpen, emitNamespaceDeclaration);
                emitNamespaceDeclaration = false;
            }

            if (leaveStreamOpen && _outputBuilderFactory.OutputBuilders.Any())
            {
                if (methodClassOutputBuilder != null)
                {
                    CloseOutputBuilder(stream, methodClassOutputBuilder, isMethodClass: true, leaveStreamOpen, emitNamespaceDeclaration);
                }

                using var sw = new StreamWriter(stream, defaultStreamWriterEncoding, DefaultStreamWriterBufferSize, leaveStreamOpen);
                sw.NewLine = "\n";

                sw.WriteLine('}');
            }

            _diagnostics.Clear();
            _outputBuilderFactory.Clear();
            _visitedCursors.Clear();
        }

        public void Dispose()
        {
            Dispose(isDisposing: true);
            GC.SuppressFinalize(this);
        }

        public void GenerateBindings(TranslationUnit translationUnit)
        {
            Debug.Assert(_outputBuilder is null);

            if (translationUnit.Handle.NumDiagnostics != 0)
            {
                var errorDiagnostics = new StringBuilder();
                errorDiagnostics.AppendLine($"The provided {nameof(CXTranslationUnit)} has the following diagnostics which prevent its use:");
                var invalidTranslationUnitHandle = false;

                for (uint i = 0; i < translationUnit.Handle.NumDiagnostics; ++i)
                {
                    using var diagnostic = translationUnit.Handle.GetDiagnostic(i);

                    if ((diagnostic.Severity == CXDiagnosticSeverity.CXDiagnostic_Error) || (diagnostic.Severity == CXDiagnosticSeverity.CXDiagnostic_Fatal))
                    {
                        invalidTranslationUnitHandle = true;
                        errorDiagnostics.Append(' ', 4);
                        errorDiagnostics.AppendLine(diagnostic.Format(CXDiagnostic.DefaultDisplayOptions).ToString());
                    }
                }

                if (invalidTranslationUnitHandle)
                {
                    throw new ArgumentOutOfRangeException(nameof(translationUnit), errorDiagnostics.ToString());
                }
            }

            var translationUnitDecl = translationUnit.TranslationUnitDecl;
            _visitedCursors.Add(translationUnitDecl);

            foreach (var decl in translationUnitDecl.Decls)
            {
                if (_config.TraversalNames.Length == 0)
                {
                    if (!decl.Location.IsFromMainFile)
                    {
                        // It is not uncommon for some declarations to be done using macros, which are themselves
                        // defined in an imported header file. We want to also check if the expansion location is
                        // in the main file to catch these cases and ensure we still generate bindings for them.

                        decl.Location.GetExpansionLocation(out CXFile file, out uint line, out uint column, out _);
                        var expansionLocation = decl.TranslationUnit.Handle.GetLocation(file, line, column);

                        if (!expansionLocation.IsFromMainFile)
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    decl.Location.GetFileLocation(out CXFile file, out _, out _, out _);
                    var fileName = file.Name.ToString();

                    if (!_config.TraversalNames.Contains(fileName))
                    {
                        continue;
                    }
                }
                Visit(decl);
            }
        }

        private void AddDiagnostic(DiagnosticLevel level, string message, Cursor cursor)
        {
            var diagnostic = new Diagnostic(level, message, cursor.Location);

            if (_diagnostics.Contains(diagnostic))
            {
                return;
            }

            _diagnostics.Add(diagnostic);
        }

        private void AddNativeTypeNameAttribute(string nativeTypeName, string prefix = null, string postfix = null, string attributePrefix = null)
        {
            if (string.IsNullOrWhiteSpace(nativeTypeName))
            {
                return;
            }

            if (prefix is null)
            {
                _outputBuilder.WriteIndentation();
            }
            else
            {
                _outputBuilder.Write(prefix);
            }

            _outputBuilder.Write('[');

            if (attributePrefix != null)
            {
                _outputBuilder.Write(attributePrefix);
            }

            _outputBuilder.Write("NativeTypeName(");
            _outputBuilder.Write('"');
            _outputBuilder.Write(nativeTypeName);
            _outputBuilder.Write('"');
            _outputBuilder.Write(")]");

            if (postfix is null)
            {
                _outputBuilder.WriteLine();
            }
            else
            {
                _outputBuilder.Write(postfix);
            }
        }

        private void CloseOutputBuilder(Stream stream, OutputBuilder outputBuilder, bool isMethodClass, bool leaveStreamOpen, bool emitNamespaceDeclaration)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (outputBuilder is null)
            {
                throw new ArgumentNullException(nameof(outputBuilder));
            }

            using var sw = new StreamWriter(stream, defaultStreamWriterEncoding, DefaultStreamWriterBufferSize, leaveStreamOpen);
            sw.NewLine = "\n";

            if (_config.GenerateMultipleFiles)
            {
                if (_config.HeaderText != string.Empty)
                {
                    sw.WriteLine(_config.HeaderText);
                }

                if (outputBuilder.UsingDirectives.Any())
                {
                    foreach (var usingDirective in outputBuilder.UsingDirectives)
                    {
                        sw.Write("using");
                        sw.Write(' ');
                        sw.Write(usingDirective);
                        sw.WriteLine(';');
                    }

                    sw.WriteLine();
                }
            }

            var indentationString = outputBuilder.IndentationString;

            if (emitNamespaceDeclaration)
            {
                sw.Write("namespace");
                sw.Write(' ');
                sw.WriteLine(Config.Namespace);
                sw.WriteLine('{');
            }
            else
            {
                sw.WriteLine();
            }

            if (isMethodClass)
            {
                sw.Write(indentationString);
                sw.Write("public static");
                sw.Write(' ');

                if (_isMethodClassUnsafe)
                {
                    sw.Write("unsafe");
                    sw.Write(' ');
                }

                sw.Write("partial class");
                sw.Write(' ');
                sw.WriteLine(Config.MethodClassName);
                sw.Write(indentationString);
                sw.WriteLine('{');

                indentationString += outputBuilder.IndentationString;

                sw.Write(indentationString);
                sw.Write("private const string LibraryPath =");
                sw.Write(' ');
                sw.Write('"');
                sw.Write(Config.LibraryPath);
                sw.Write('"');
                sw.WriteLine(';');
                sw.WriteLine();
            }

            foreach (var line in outputBuilder.Contents)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    sw.WriteLine();
                }
                else
                {
                    sw.Write(indentationString);
                    sw.WriteLine(line);
                }
            }

            if (isMethodClass)
            {
                sw.Write(outputBuilder.IndentationString);
                sw.WriteLine('}');
            }

            if (_config.GenerateMultipleFiles)
            {
                sw.WriteLine('}');
            }
        }

        private void Dispose(bool isDisposing)
        {
            Debug.Assert(_outputBuilder is null);

            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (isDisposing)
            {
                Close();
            }
            _index.Dispose();
        }

        private string EscapeName(string name)
        {
            switch (name)
            {
                case "abstract":
                case "as":
                case "base":
                case "bool":
                case "break":
                case "byte":
                case "case":
                case "catch":
                case "char":
                case "checked":
                case "class":
                case "const":
                case "continue":
                case "decimal":
                case "default":
                case "delegate":
                case "do":
                case "double":
                case "else":
                case "enum":
                case "event":
                case "explicit":
                case "extern":
                case "false":
                case "finally":
                case "fixed":
                case "float":
                case "for":
                case "foreach":
                case "goto":
                case "if":
                case "implicit":
                case "in":
                case "int":
                case "interface":
                case "internal":
                case "is":
                case "lock":
                case "long":
                case "namespace":
                case "new":
                case "null":
                case "object":
                case "operator":
                case "out":
                case "override":
                case "params":
                case "private":
                case "protected":
                case "public":
                case "readonly":
                case "ref":
                case "return":
                case "sbyte":
                case "sealed":
                case "short":
                case "sizeof":
                case "stackalloc":
                case "static":
                case "string":
                case "struct":
                case "switch":
                case "this":
                case "throw":
                case "true":
                case "try":
                case "typeof":
                case "uint":
                case "ulong":
                case "unchecked":
                case "unsafe":
                case "ushort":
                case "using":
                case "using static":
                case "virtual":
                case "void":
                case "volatile":
                case "while":
                {
                    return $"@{name}";
                }

                default:
                {
                    return name;
                }
            }
        }

        private string EscapeAndStripName(string name)
        {
            if (name.StartsWith(_config.MethodPrefixToStrip))
            {
                name = name.Substring(_config.MethodPrefixToStrip.Length);
            }

            return EscapeName(name);
        }

        private string GetAccessSpecifierName(NamedDecl namedDecl)
        {
            string name;

            switch (namedDecl.Access)
            {
                case CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier:
                {
                    // Top level declarations will have an invalid access specifier
                    name = "public";
                    break;
                }

                case CX_CXXAccessSpecifier.CX_CXXPublic:
                {
                    name = "public";
                    break;
                }

                case CX_CXXAccessSpecifier.CX_CXXProtected:
                {
                    name = "protected";
                    break;
                }

                case CX_CXXAccessSpecifier.CX_CXXPrivate:
                {
                    name = "private";
                    break;
                }

                default:
                {
                    name = "internal";
                    AddDiagnostic(DiagnosticLevel.Warning, $"Unknown access specifier: '{namedDecl.Access}'. Falling back to '{name}'.", namedDecl);
                    break;
                }
            }

            return name;
        }

        private string GetArtificalFixedSizedBufferName(FieldDecl fieldDecl)
        {
            var name = GetRemappedCursorName(fieldDecl);
            return $"_{name}_e__FixedBuffer";
        }

        private string GetCallingConventionName(Cursor cursor, CXCallingConv callingConvention)
        {
            switch (callingConvention)
            {
                case CXCallingConv.CXCallingConv_C:
                {
                    return "Cdecl";
                }

                case CXCallingConv.CXCallingConv_X86StdCall:
                {
                    return "StdCall";
                }

                case CXCallingConv.CXCallingConv_X86FastCall:
                {
                    return "FastCall";
                }

                case CXCallingConv.CXCallingConv_X86ThisCall:
                {
                    return "ThisCall";
                }

                case CXCallingConv.CXCallingConv_Win64:
                {
                    return "Winapi";
                }

                default:
                {
                    var name = "WinApi";
                    AddDiagnostic(DiagnosticLevel.Info, $"Unsupported calling convention: '{callingConvention}'. Falling back to '{name}'.", cursor);
                    return name;
                }
            }
        }

        private string GetCursorName(NamedDecl namedDecl)
        {
            var name = namedDecl.Name;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (namedDecl is TypeDecl typeDecl)
                {
                    if ((typeDecl is TagDecl tagDecl) && tagDecl.Handle.IsAnonymous)
                    {
                        namedDecl.Location.GetFileLocation(out var file, out var _, out var _, out var offset);
                        var fileName = Path.GetFileNameWithoutExtension(file.Name.ToString());
                        name = $"__Anonymous{tagDecl.TypeForDecl.KindSpelling}_{fileName}_{offset}";
                        AddDiagnostic(DiagnosticLevel.Info, $"Anonymous declaration found in '{nameof(GetCursorName)}'. Falling back to '{name}'.", namedDecl);
                    }
                    else
                    {
                        name = GetTypeName(namedDecl, typeDecl.TypeForDecl, out var nativeTypeName);
                        Debug.Assert(string.IsNullOrWhiteSpace(nativeTypeName));
                    }
                }
                else if (namedDecl is ParmVarDecl)
                {
                    name = "param";
                }
                else
                {
                    AddDiagnostic(DiagnosticLevel.Error, $"Unsupported anonymous named declaration: '{namedDecl.Kind}'.", namedDecl);
                }
            }

            Debug.Assert(!string.IsNullOrWhiteSpace(name));
            return name;
        }

        private string GetRemappedCursorName(NamedDecl namedDecl)
        {
            var name = GetCursorName(namedDecl);
            return GetRemappedName(name);
        }

        private string GetRemappedName(string name)
        {
            if (!_config.RemappedNames.TryGetValue(name, out string remappedName))
            {
                remappedName = name;
            }

            if (remappedName.Equals("IntPtr") || remappedName.Equals("UIntPtr"))
            {
                _outputBuilder.AddUsingDirective("System");
            }

            return remappedName;
        }

        private string GetRemappedTypeName(Cursor cursor, Type type, out string nativeTypeName)
        {
            var name = GetTypeName(cursor, type, out nativeTypeName);
            name = GetRemappedName(name);

            if (nativeTypeName.Equals(name))
            {
                nativeTypeName = string.Empty;
            }
            return name;
        }

        private string GetTypeName(Cursor cursor, Type type, out string nativeTypeName)
        {
            var name = type.AsString;
            nativeTypeName = name;

            if (type is ArrayType arrayType)
            {
                name = GetTypeName(cursor, arrayType.ElementType, out var nativeElementTypeName);
            }
            else if (type is AttributedType attributedType)
            {
                name = GetTypeName(cursor, attributedType.ModifiedType, out var nativeModifiedTypeName);
            }
            else if (type is BuiltinType)
            {
                switch (type.Kind)
                {
                    case CXTypeKind.CXType_Void:
                    {
                        name = "void";
                        break;
                    }

                    case CXTypeKind.CXType_Bool:
                    {
                        name = "bool";
                        break;
                    }

                    case CXTypeKind.CXType_Char_U:
                    case CXTypeKind.CXType_UChar:
                    {
                        name = "byte";
                        break;
                    }

                    case CXTypeKind.CXType_UShort:
                    {
                        name = "ushort";
                        break;
                    }

                    case CXTypeKind.CXType_UInt:
                    {
                        name = "uint";
                        break;
                    }

                    case CXTypeKind.CXType_ULong:
                    {
                        name = _config.GenerateUnixTypes ? "UIntPtr" : "uint";
                        break;
                    }

                    case CXTypeKind.CXType_ULongLong:
                    {
                        name = "ulong";
                        break;
                    }

                    case CXTypeKind.CXType_Char_S:
                    case CXTypeKind.CXType_SChar:
                    {
                        name = "sbyte";
                        break;
                    }

                    case CXTypeKind.CXType_WChar:
                    {
                        name = _config.GenerateUnixTypes ? "int" : "ushort";
                        break;
                    }

                    case CXTypeKind.CXType_Short:
                    {
                        name = "short";
                        break;
                    }

                    case CXTypeKind.CXType_Int:
                    {
                        name = "int";
                        break;
                    }

                    case CXTypeKind.CXType_Long:
                    {
                        name = _config.GenerateUnixTypes ? "IntPtr" : "int";
                        break;
                    }

                    case CXTypeKind.CXType_LongLong:
                    {
                        name = "long";
                        break;
                    }

                    case CXTypeKind.CXType_Float:
                    {
                        name = "float";
                        break;
                    }

                    case CXTypeKind.CXType_Double:
                    {
                        name = "double";
                        break;
                    }

                    default:
                    {
                        AddDiagnostic(DiagnosticLevel.Warning, $"Unsupported builtin type: '{type.TypeClass}'. Falling back '{name}'.", cursor);
                        break;
                    }
                }
            }
            else if (type is ElaboratedType elaboratedType)
            {
                name = GetTypeName(cursor, elaboratedType.NamedType, out var nativeNamedTypeName);
            }
            else if (type is PointerType pointerType)
            {
                name = GetTypeNameForPointeeType(cursor, pointerType.PointeeType, out var nativePointeeTypeName);
            }
            else if (type is ReferenceType referenceType)
            {
                name = GetTypeNameForPointeeType(cursor, referenceType.PointeeType, out var nativePointeeTypeName);
            }
            else if (type is TypedefType typedefType)
            {
                // We check remapped names here so that types that have variable sizes
                // can be treated correctly. Otherwise, they will resolve to a particular
                // platform size, based on whatever parameters were passed into clang.

                if (_config.RemappedNames.TryGetValue(name, out string remappedName))
                {
                    name = remappedName;
                }
                else
                {
                    name = GetTypeName(cursor, typedefType.Decl.UnderlyingType, out var nativeUnderlyingTypeName);
                }
            }
            else if (!(type is FunctionType) && !(type is TagType))
            {
                AddDiagnostic(DiagnosticLevel.Warning, $"Unsupported type: '{type.TypeClass}'. Falling back '{name}'.", cursor);
            }

            Debug.Assert(!string.IsNullOrWhiteSpace(name));
            Debug.Assert(!string.IsNullOrWhiteSpace(nativeTypeName));

            if (nativeTypeName.Equals(name))
            {
                nativeTypeName = string.Empty;
            }
            return name;
        }

        private string GetTypeNameForPointeeType(Cursor cursor, Type pointeeType, out string nativePointeeTypeName)
        {
            var name = pointeeType.AsString;
            nativePointeeTypeName = name;

            if (pointeeType is AttributedType attributedType)
            {
                name = GetTypeNameForPointeeType(cursor, attributedType.ModifiedType, out var nativeModifiedTypeName);
            }
            else if (pointeeType is FunctionType)
            {
                name = "IntPtr";
            }
            else
            {
                name = GetTypeName(cursor, pointeeType, out nativePointeeTypeName);
                name += '*';
            }

            return name;
        }

        private bool IsSupportedFixedSizedBufferType(string typeName)
        {
            switch (typeName)
            {
                case "bool":
                case "byte":
                case "char":
                case "double":
                case "float":
                case "int":
                case "long":
                case "sbyte":
                case "short":
                case "ushort":
                case "uint":
                case "ulong":
                {
                    return true;
                }

                default:
                {
                    return false;
                }
            }
        }

        private bool IsUnsafe(FieldDecl fieldDecl)
        {
            var type = fieldDecl.Type;

            if (type is ConstantArrayType)
            {
                var typeName = GetRemappedTypeName(fieldDecl, type, out _);
                return IsSupportedFixedSizedBufferType(typeName);
            }

            return IsUnsafe(fieldDecl, type);
        }

        private bool IsUnsafe(FunctionDecl functionDecl)
        {
            var returnType = functionDecl.ReturnType;
            var returnTypeName = GetRemappedTypeName(functionDecl, returnType, out _);

            if (returnTypeName.Contains('*'))
            {
                return true;
            }

            foreach (var parmVarDecl in functionDecl.Parameters)
            {
                if (IsUnsafe(parmVarDecl))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsUnsafe(ParmVarDecl parmVarDecl)
        {
            var type = parmVarDecl.Type;
            return IsUnsafe(parmVarDecl, type);
        }

        private bool IsUnsafe(RecordDecl recordDecl)
        {
            foreach (var fieldDecl in recordDecl.Fields)
            {
                if (IsUnsafe(fieldDecl))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsUnsafe(TypedefDecl typedefDecl, FunctionProtoType functionProtoType)
        {
            var returnType = functionProtoType.ReturnType;

            if (IsUnsafe(typedefDecl, returnType))
            {
                return true;
            }

            foreach (var paramType in functionProtoType.ParamTypes)
            {
                if (IsUnsafe(typedefDecl, paramType))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsUnsafe(NamedDecl namedDecl, Type type)
        {
            var typeName = GetRemappedTypeName(namedDecl, type, out _);
            return typeName.Contains('*');
        }

        private void StartUsingOutputBuilder(string name)
        {
            if (_outputBuilder != null)
            {
                Debug.Assert(_outputBuilderUsers >= 1);
                _outputBuilderUsers++;

                _outputBuilder.WriteLine();
                return;
            }

            Debug.Assert(_outputBuilderUsers == 0);

            if (!_outputBuilderFactory.TryGetOutputBuilder(name, out _outputBuilder))
            {
                _outputBuilder = _outputBuilderFactory.Create(name);
            }
            else
            {
                _outputBuilder.WriteLine();
            }
            _outputBuilderUsers++;
        }

        private void StopUsingOutputBuilder()
        {
            if (_outputBuilderUsers == 1)
            {
                _outputBuilder = null;
            }
            _outputBuilderUsers--;
        }

        private void Visit(Cursor cursor)
        {
            if (_visitedCursors.Contains(cursor))
            {
                return;
            }

            _visitedCursors.Add(cursor);

            if (cursor is Attr attr)
            {
                VisitAttr(attr);
            }
            else if (cursor is Decl decl)
            {
                VisitDecl(decl);
            }
            else if (cursor is Ref @ref)
            {
                VisitRef(@ref);
            }
            else if (cursor is Stmt stmt)
            {
                VisitStmt(stmt);
            }
            else
            {
                AddDiagnostic(DiagnosticLevel.Error, $"Unsupported cursor: '{cursor.CursorKindSpelling}'. Generated bindings may be incomplete.", cursor);
            }
        }
    }
}