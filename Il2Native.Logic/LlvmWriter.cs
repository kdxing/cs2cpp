﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="LlvmWriter.cs" company="">
//   
// </copyright>
// <summary>
//   
// </summary>
// --------------------------------------------------------------------------------------------------------------------
namespace Il2Native.Logic
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Text;

    using Il2Native.Logic.CodeParts;
    using Il2Native.Logic.Exceptions;
    using Il2Native.Logic.Gencode;
    using Il2Native.Logic.Properties;

    using PEAssemblyReader;

    using Roslyn.Utilities;

    using OpCodesEmit = System.Reflection.Emit.OpCodes;

    /// <summary>
    /// </summary>
    public class LlvmWriter : BaseWriter, ICodeWriter
    {
        /// <summary>
        /// </summary>
        public static int PointerSize = 4;

        /// <summary>
        /// </summary>
        public Stack<CatchOfFinallyClause> catchScopes = new Stack<CatchOfFinallyClause>();

        /// <summary>
        /// </summary>
        public bool landingPadVariablesAreWritten;

        /// <summary>
        /// </summary>
        public bool needToWriteUnreachable;

        /// <summary>
        /// </summary>
        public bool needToWriteUnwindException;

        /// <summary>
        /// </summary>
        public Stack<TryClause> tryScopes = new Stack<TryClause>();

        /// <summary>
        /// </summary>
        public readonly HashSet<IType> typeRttiDeclRequired = new HashSet<IType>();

        /// <summary>
        /// </summary>
        public readonly HashSet<IType> typeRttiPointerDeclRequired = new HashSet<IType>();

        /// <summary>
        /// </summary>
        private static int methodNumberIncremental;

        /// <summary>
        /// </summary>
        private readonly IDictionary<string, int> indexByFieldInfo = new SortedDictionary<string, int>();

        /// <summary>
        /// </summary>
        private readonly HashSet<IMethod> methodDeclRequired = new HashSet<IMethod>();

        /// <summary>
        /// </summary>
        private readonly HashSet<IMethod> processedMethods = new HashSet<IMethod>();

        /// <summary>
        /// </summary>
        private readonly HashSet<IType> processedRttiPointerTypes = new HashSet<IType>();

        /// <summary>
        /// </summary>
        private readonly HashSet<IType> processedRttiTypes = new HashSet<IType>();

        /// <summary>
        /// </summary>
        private readonly HashSet<IType> processedTypes = new HashSet<IType>();

        /// <summary>
        /// </summary>
        private int resultNumberIncremental;

        /// <summary>
        /// </summary>
        private int blockJumpAddressIncremental;

        /// <summary>
        /// </summary>
        private readonly IList<IField> staticFieldsInfo = new List<IField>();

        /// <summary>
        /// </summary>
        private int stringIndexIncremental;

        /// <summary>
        /// </summary>
        private readonly IDictionary<int, string> stringStorage = new SortedDictionary<int, string>();

        /// <summary>
        /// </summary>
        private readonly HashSet<IType> typeDeclRequired = new HashSet<IType>();

        /// <summary>
        /// </summary>
        /// <param name="fileName">
        /// </param>
        /// <param name="args">
        /// </param>
        public LlvmWriter(string fileName, string[] args)
        {
            var extension = Path.GetExtension(fileName);
            var outputFile = extension != null && extension.Equals(string.Empty) ? fileName + ".ll" : fileName;
            this.Output = new LlvmIndentedTextWriter(new StreamWriter(outputFile));
            this.IncludeMiniCoreLib = args != null && args.Contains("includeMiniCore");
        }

        /// <summary>
        /// </summary>
        public LlvmIndentedTextWriter Output { get; private set; }

        /// <summary>
        /// </summary>
        public bool IncludeMiniCoreLib { get; set; }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="used">
        /// </param>
        /// <param name="parameterInfos">
        /// </param>
        /// <param name="isVirtual">
        /// </param>
        /// <param name="hasThis">
        /// </param>
        /// <param name="isCtor">
        /// </param>
        /// <param name="isDirectValue">
        /// </param>
        /// <param name="resultNumberForThis">
        /// </param>
        /// <param name="thisType">
        /// </param>
        /// <param name="resultNumberForReturn">
        /// </param>
        /// <param name="returnType">
        /// </param>
        public void ActualWrite(
            LlvmIndentedTextWriter writer,
            OpCodePart[] used,
            IEnumerable<IParameter> parameterInfos,
            bool @isVirtual,
            bool hasThis,
            bool isCtor,
            IList<bool> isDirectValue,
            LlvmResult resultNumberForThis,
            IType thisType,
            FullyDefinedReference resultNumberForReturn,
            IType returnType)
        {
            writer.Write("(");

            var index = 0;

            var returnIsStruct = returnType != null && returnType.IsStructureType();

            // allocate space for structure if return type is structure
            if (returnIsStruct)
            {
                returnType.WriteTypePrefix(writer, returnType.IsStructureType());
                writer.Write(' ');
                if (resultNumberForReturn != null)
                {
                    WriteResultNumber(resultNumberForReturn);
                }
            }

            if (hasThis)
            {
                if (returnIsStruct)
                {
                    writer.Write(", ");
                }

                thisType.UseAsClass = true;

                thisType.WriteTypePrefix(writer, thisType.IsStructureType());
                writer.Write(' ');
                if (resultNumberForThis != null)
                {
                    WriteResultNumber(resultNumberForThis);
                }
                else if (used != null && used.Length > 0)
                {
                    if (used[0].HasResult)
                    {
                        WriteResultNumber(used[0].Result);
                    }
                    else if (isDirectValue[0])
                    {
                        this.ActualWrite(writer, used[0]);
                    }
                }
            }

            this.CheckIfExternalDeclarationIsRequired(returnType);

            foreach (var parameter in parameterInfos)
            {
                this.CheckIfExternalDeclarationIsRequired(parameter.ParameterType);

                if (hasThis || index > 0 || returnIsStruct)
                {
                    writer.Write(", ");
                }

                var effectiveIndex = index + (@isVirtual || (hasThis && !isCtor) ? 1 : 0);

                var parameterInput = used[effectiveIndex];

                parameter.ParameterType.WriteTypePrefix(writer, parameter.ParameterType.IsStructureType());
                if (parameter.ParameterType.IsStructureType())
                {
                    writer.Write(" byval align " + PointerSize);
                }

                writer.Write(' ');

                if (!isDirectValue[effectiveIndex])
                {
                    WriteResultNumber(used[effectiveIndex]);
                }
                else
                {
                    this.ActualWrite(writer, parameterInput);
                }

                index++;
            }

            writer.Write(")");
        }

        /// <summary>
        /// </summary>
        /// <param name="methodBase">
        /// </param>
        public void CheckIfExternalDeclarationIsRequired(IMethod methodBase)
        {
            var mi = methodBase;
            if (mi != null)
            {
                if (mi.DeclaringType.AssemblyQualifiedName != this.ThisType.AssemblyQualifiedName)
                {
                    this.methodDeclRequired.Add(methodBase);
                }

                return;
            }

            var ci = methodBase as IConstructor;
            if (ci != null)
            {
                if (ci.DeclaringType.AssemblyQualifiedName != this.ThisType.AssemblyQualifiedName)
                {
                    this.methodDeclRequired.Add(methodBase);
                }

                return;
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        public void CheckIfExternalDeclarationIsRequired(IType type)
        {
            if (type != null)
            {
                if (type.AssemblyQualifiedName != this.AssemblyQualifiedName)
                {
                    this.typeDeclRequired.Add(type);
                }
            }
        }

        /// <summary>
        /// </summary>
        public void Close()
        {
            this.Output.Close();
        }

        /// <summary>
        /// </summary>
        /// <param name="index">
        /// </param>
        /// <returns>
        /// </returns>
        public string GetLocalVarName(int index)
        {
            return string.Concat("%local", index);
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        /// <returns>
        /// </returns>
        public bool IsDirectValue(OpCodePart opCode)
        {
            if (opCode is OpCodeBlock)
            {
                return false;
            }

            if (opCode.Any(Code.Dup))
            {
                return this.IsDirectValue(opCode.OpCodeOperands[0]);
            }

            if (opCode.Any(Code.Ldsfld) && (opCode as OpCodeFieldInfoPart).Operand.FieldType.IsStructureType())
            {
                return true;
            }

            if (opCode.Any(Code.Ldloc, Code.Ldloc_0, Code.Ldloc_1, Code.Ldloc_2, Code.Ldloc_3, Code.Ldloc_S))
            {
                var localType = opCode.GetLocalType(this);
                var skip = localType.IsStructureType() && opCode.DestinationName == null;
                if (skip)
                {
                    // it means that we are working with a Struct and are going to pass it as an address
                    return true;
                }
            }

            if (opCode.Any(Code.Ldarg, Code.Ldarg_0, Code.Ldarg_1, Code.Ldarg_2, Code.Ldarg_3, Code.Ldarg_S))
            {
                var index = opCode.GetArgIndex();

                if (!(this.HasMethodThis && index == 0))
                {
                    var parameterType = this.GetArgType(index);
                    var skip = parameterType.IsStructureType() && opCode.DestinationName == null;
                    if (skip)
                    {
                        // it means that we are working with a Struct and are going to pass it as an address
                        return true;
                    }
                }
            }

            if (opCode.Any(Code.Ldstr))
            {
                var opCodeString = opCode as OpCodeStringPart;
                return opCodeString.StringIndex > 0;
            }

            if (opCode.Any(Code.Ldloca, Code.Ldloca_S))
            {
                var localType = opCode.GetLocalType(this);
                return !localType.IsPointer;
            }

            if (opCode.Any(Code.Ldarga, Code.Ldarga_S))
            {
                var index = opCode.GetArgIndex();
                if (this.HasMethodThis && index == 0)
                {
                    return true;
                }

                var parameterType = this.GetArgType(index);
                return !parameterType.IsPointer;
            }

            if (opCode.Any(Code.Conv_U))
            {
                return opCode.OpCodeOperands[0].UseAsNull || opCode.OpCodeOperands[0].Any(Code.Ldloca, Code.Ldloca_S, Code.Ldarga, Code.Ldarga_S, Code.Ldflda);
            }

            if (opCode.Any(Code.Conv_I))
            {
                return this.IsDirectValue(opCode.OpCodeOperands[0]);
            }

            // TODO: when finish remove Ldtoken from the list of Direct Values and I think Ldstr as well
            return opCode.Any(
                Code.Ldc_I4_0,
                Code.Ldc_I4_1,
                Code.Ldc_I4_2,
                Code.Ldc_I4_3,
                Code.Ldc_I4_4,
                Code.Ldc_I4_5,
                Code.Ldc_I4_6,
                Code.Ldc_I4_7,
                Code.Ldc_I4_8,
                Code.Ldc_I4_M1,
                Code.Ldc_I4,
                Code.Ldc_I4_S,
                Code.Ldc_I8,
                Code.Ldc_R4,
                Code.Ldc_R8,
                Code.Ldnull,
                Code.Ldtoken,
                Code.Ldsflda,
                Code.Ldftn);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="index">
        /// </param>
        /// <param name="directResult">
        /// </param>
        /// <param name="detectAndWriteTypePrefix">
        /// </param>
        public void PostProcessOperand(LlvmIndentedTextWriter writer, OpCodePart opCode, int index, bool directResult, bool detectAndWriteTypePrefix = false)
        {
            if (opCode.OpCodeOperands == null || opCode.OpCodeOperands.Length == 0)
            {
                return;
            }

            var operand = opCode.OpCodeOperands[index];
            this.PostProcess(writer, operand, directResult, detectAndWriteTypePrefix);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="index">
        /// </param>
        /// <param name="options">
        /// </param>
        /// <returns>
        /// </returns>
        public bool PreProcessOperand(LlvmIndentedTextWriter writer, OpCodePart opCode, int index, OperandOptions options = OperandOptions.None)
        {
            if (opCode.OpCodeOperands == null || opCode.OpCodeOperands.Length == 0)
            {
                return false;
            }

            var operandOpCode = opCode.OpCodeOperands[index];
            return this.PreProcess(writer, operandOpCode, options: options);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="op">
        /// </param>
        /// <param name="requiredType">
        /// </param>
        /// <param name="resultType">
        /// </param>
        /// <param name="options">
        /// </param>
        public void ProcessOperator(
            LlvmIndentedTextWriter writer,
            OpCodePart opCode,
            string op,
            IType requiredType = null,
            IType resultType = null,
            OperandOptions options = OperandOptions.None,
            int operand1 = 0,
            int operand2 = 1)
        {
            IType castFrom;
            IType intAdjustment;
            bool intAdjustSecondOperand;
            var effectiveType = this.DetectTypePrefix(opCode, requiredType, options, out castFrom, out intAdjustment, out intAdjustSecondOperand, operand1, operand2);
            if (castFrom != null && opCode.OpCodeOperands[operand1].HasResult)
            {
                this.WriteCast(opCode.OpCodeOperands[operand1], opCode.OpCodeOperands[operand1].Result, effectiveType);
            }

            if (intAdjustment != null && opCode.OpCodeOperands[operand1].HasResult)
            {
                var changeType = this.AdjustIntConvertableTypes(
                    writer,
                    opCode.OpCodeOperands[
                        opCode.OpCodeOperands.Length > operand2 && (intAdjustSecondOperand || options.HasFlag(OperandOptions.TypeIsInSecondOperand))
                            ? operand2
                            : operand1],
                    false,
                    intAdjustment);

                if (changeType && resultType == null)
                {
                    resultType = intAdjustment;
                    effectiveType = intAdjustment;
                }
            }

            if ((opCode.OpCode.StackBehaviourPush != StackBehaviour.Push0 || options.HasFlag(OperandOptions.GenerateResult))
                && op != "store")
            {
                var resultOf = this.ResultOf(opCode);
                this.WriteSetResultNumber(opCode, resultType ?? (resultOf != null ? resultOf.IType : requiredType));
            }

            writer.Write(op);
            writer.Write(' ');

            if (!options.HasFlag(OperandOptions.NoTypePrefix) && !options.HasFlag(OperandOptions.IgnoreOperand))
            {
                var type = effectiveType ?? this.ResolveType("System.Void");
                type.WriteTypePrefix(writer);
                if (options.HasFlag(OperandOptions.AppendPointer))
                {
                    writer.Write('*');
                }
            }
        }

        /// <summary>
        /// </summary>
        public void StartProcess()
        {
            base.StartProcess();
            this.resultNumberIncremental = 0;
            this.blockJumpAddressIncremental = 0;
            this.landingPadVariablesAreWritten = false;
            this.needToWriteUnwindException = false;
            this.needToWriteUnreachable = false;
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="op">
        /// </param>
        /// <param name="requiredType">
        /// </param>
        /// <param name="resultType">
        /// </param>
        /// <param name="options">
        /// </param>
        public void UnaryOper(
            LlvmIndentedTextWriter writer,
            OpCodePart opCode,
            string op,
            IType requiredType = null,
            IType resultType = null,
            OperandOptions options = OperandOptions.None)
        {
            this.UnaryOper(writer, opCode, 0, op, requiredType, resultType, options);
        }

        /// <summary>
        /// </summary>
        /// <param name="rawText">
        /// </param>
        public void Write(string rawText)
        {
            this.Output.Write(rawText);
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        public void Write(OpCodePart opCode, bool doNotWrite = false)
        {
            ////this.Output.WriteLine("; {0}", opCode.OpCode.Name);
            this.Process(opCode, doNotWrite);
        }

        /// <summary>
        /// </summary>
        public void WriteAfterConstructors()
        {
        }

        /// <summary>
        /// </summary>
        /// <param name="count">
        /// </param>
        /// <param name="disablePostDeclarations">
        /// </param>
        public void WriteAfterFields(int count, bool disablePostDeclarations = false)
        {
            this.Output.WriteLine(string.Empty);

            this.Output.Indent--;
            this.Output.WriteLine("}");

            if (!disablePostDeclarations)
            {
                this.WritePostDeclarations();

                this.Output.WriteLine(string.Empty);
                this.ThisType.WriteRtti(this);

                this.processedTypes.Add(this.ThisType);
                this.processedRttiTypes.Add(this.ThisType);
                this.processedRttiPointerTypes.Add(this.ThisType);

                this.Output.WriteLine(string.Empty);
                this.ThisType.WriteInitObjectMethod(this);

                var stored = this.ThisType.UseAsClass;
                this.ThisType.UseAsClass = false;

                if ((this.ThisType.IsPrimitiveType() || this.ThisType.IsStructureType() || this.ThisType.IsEnum) 
                    && this.ThisType.FullName != "System.Enum" 
                    && this.ThisType.FullName != "System.IntPtr" 
                    && this.ThisType.FullName != "System.UIntPtr")
                {
                    this.ThisType.WriteBoxMethod(this);
                    this.ThisType.WriteUnboxMethod(this);
                }

                this.ThisType.UseAsClass = stored;
            }
        }

        /// <summary>
        /// </summary>
        public void WriteAfterMethods()
        {
        }

        /// <summary>
        /// </summary>
        public void WriteBeforeConstructors()
        {
            this.Output.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        /// <param name="count">
        /// </param>
        public void WriteBeforeFields(int count)
        {
            var baseType = this.ThisType.BaseType;

            this.CheckIfExternalDeclarationIsRequired(baseType);

            this.Output.WriteLine("{");
            this.Output.Indent++;

            var interfacesList = this.ThisType.GetInterfaces();

            // put virtual root table if type has no any base with virtual types
            if (this.ThisType.IsInterface)
            {
                if (!interfacesList.Any())
                {
                    this.Output.WriteLine("i32 (...)**");
                }
            }
            else if (this.ThisType.IsRootOfVirtualTable())
            {
                this.Output.WriteLine("i32 (...)**");
            }

            if (baseType != null)
            {
                baseType.WriteTypeWithoutModifiers(this.Output);
            }

            var index = 0;
            foreach (var @interface in interfacesList)
            {
                if (this.ThisType.BaseType != null && this.ThisType.BaseType.GetInterfaces().Contains(@interface))
                {
                    continue;
                }

                ////this.WriteTypeDefinitionIfNotWrittenYet(@interface);
                this.CheckIfExternalDeclarationIsRequired(@interface);

                this.Output.WriteLine(index == 0 && baseType == null ? string.Empty : ", ");
                @interface.WriteTypeWithoutModifiers(this.Output);
                index++;
            }
        }

        /// <summary>
        /// </summary>
        public void WriteBeforeMethods()
        {
        }

        /// <summary>
        /// </summary>
        /// <param name="ctor">
        /// </param>
        public void WriteConstructorEnd(IConstructor ctor, bool doNotWrite = false)
        {
            this.WriteMethodBody(doNotWrite);

            if (doNotWrite)
            {
                return;
            }

            if (!this.NoBody)
            {
                this.Output.EndMethodBody();

                this.Output.Indent--;

                this.Output.WriteLine("}");
            }

            this.Output.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        /// <param name="ctor">
        /// </param>
        public void WriteConstructorStart(IConstructor ctor, bool doNotWrite = false)
        {
            this.StartProcess();

            if (doNotWrite)
            {
                this.ReadMethodInfo(ctor);
                return;
            }

            this.WriteRequiredTypesForBody();

            this.processedMethods.Add(ctor);

            this.ReadMethodInfo(ctor);

            if (ctor.IsAbstract || ctor.GetMethodBody() == null)
            {
                this.Output.Write("declare ");
            }
            else
            {
                this.Output.Write("define ");
            }

            this.Output.Write("void ");

            // this.WriteMethodName(this.Output, ctor);
            this.WriteMethodDefinitionName(this.Output, ctor);
            if (ctor.IsStatic)
            {
                this.StaticConstructors.Add(ctor);
            }

            this.WriteMethodParamsDef(this.Output, ctor.GetParameters(), this.HasMethodThis, this.ThisType, this.ResolveType("System.Void"));

            this.WriteMethodNumber();

            // write local declarations
            var methodBase = ctor.GetMethodBody();
            if (methodBase != null)
            {
                this.Output.WriteLine(" {");
                this.Output.Indent++;
                this.WriteLocalVariableDeclarations(methodBase.LocalVariables);
                this.WriteArgumentCopyDeclarations(ctor.GetParameters(), this.HasMethodThis);

                this.Output.StartMethodBody();
            }

            methodNumberIncremental++;
        }

        private void WriteRequiredTypesForBody()
        {
            // get all required types for methods bodies
            foreach (var requiredType in this.requiredTypesForBody)
            {
                this.WriteTypeDefinitionIfNotWrittenYet(requiredType);
            }

            // clear types for next type
            this.requiredTypesForBody.Clear();
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="type">
        /// </param>
        /// <param name="sourceVarName">
        /// </param>
        /// <param name="desctVarName">
        /// </param>
        public void WriteCopyStruct(LlvmIndentedTextWriter writer, OpCodePart opCode, IType type, string sourceVarName, string desctVarName)
        {
            var storeResult = opCode.Result;

            this.WriteBitcast(opCode, type, desctVarName);
            var op1 = opCode.Result;
            writer.WriteLine(string.Empty);
            this.WriteBitcast(opCode, type, sourceVarName);
            var op2 = opCode.Result;
            writer.WriteLine(string.Empty);

            this.WriteMemCopy(type, op1, op2);

            opCode.Result = storeResult;
        }

        /// <summary>
        /// </summary>
        public void WriteEnd()
        {
            if (this.MainMethod != null)
            {
                this.Output.Write("define i32 @main()");

                this.WriteMethodNumber();

                this.Output.WriteLine(" {");

                this.Output.Indent++;

                if (!this.MainMethod.ReturnType.IsVoid())
                {
                    this.Output.Write("%1 = call i32 ");
                }
                else
                {
                    this.Output.Write("call void ");
                }

                var parameters = this.MainMethod.GetParameters();

                this.WriteMethodDefinitionName(this.Output, this.MainMethod);
                this.Output.Write("(");

                var index = 0;
                foreach (var parameter in parameters)
                {
                    if (index > 0)
                    {
                        this.Output.Write(", ");
                    }

                    this.Output.Write("%\"System.String\"** null");

                    index++;
                }

                this.Output.WriteLine(");");

                if (!this.MainMethod.ReturnType.IsVoid())
                {
                    this.Output.WriteLine("ret i32 %1");
                }
                else
                {
                    this.Output.WriteLine("ret i32 0");
                }

                this.Output.Indent--;
                this.Output.WriteLine("}");
            }

            // write global ctors caller
            this.Output.WriteLine(string.Empty);
            this.Output.WriteLine("define internal void @_GLOBAL_CTORS_EXECUTE_() {");
            this.Output.Indent++;
            foreach (var staticCtor in this.StaticConstructors)
            {
                this.Output.WriteLine("call void {0}()", this.GetFullMethodName(staticCtor));
            }

            this.Output.WriteLine("ret void");
            this.Output.Indent--;
            this.Output.WriteLine("}");

            if (!this.IncludeMiniCoreLib)
            {
                this.WriteRequiredDeclarations();
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="field">
        /// </param>
        /// <param name="number">
        /// </param>
        /// <param name="count">
        /// </param>
        public void WriteFieldEnd(IField field, int number, int count)
        {
        }

        /// <summary>
        /// </summary>
        /// <param name="field">
        /// </param>
        /// <param name="number">
        /// </param>
        /// <param name="count">
        /// </param>
        public void WriteFieldStart(IField field, int number, int count)
        {
            if (field.IsStatic)
            {
                if (!field.IsLiteral)
                {
                    this.staticFieldsInfo.Add(field);
                }

                return;
            }

            this.Output.WriteLine(',');

            field.FieldType.WriteTypePrefix(this.Output, false);

            // this.Output.Write(' ');
            // this.Output.Write(field.Name);
            // this.Output.WriteLine(';');
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        /// <param name="number">
        /// </param>
        /// <param name="count">
        /// </param>
        public void WriteForwardDeclaration(IType type, int number, int count)
        {
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCodeMethodInfo">
        /// </param>
        /// <param name="methodInfo">
        /// </param>
        /// <param name="thisType">
        /// </param>
        /// <param name="pointerToInterfaceVirtualTablePointersResultNumber">
        /// </param>
        public void WriteGetThisPointerFromInterfacePointer(
            LlvmIndentedTextWriter writer,
            OpCodePart opCodeMethodInfo,
            IMethod methodInfo,
            IType thisType,
            LlvmResult pointerToInterfaceVirtualTablePointersResultNumber)
        {
            writer.WriteLine("; Get 'this' from Interface Virtual Table");

            this.WriteGetInterfaceOffsetToObjectRootPointer(writer, opCodeMethodInfo, methodInfo);

            writer.WriteLine(string.Empty);
            var offsetAddressAsIntResultNumber = opCodeMethodInfo.Result;

            // get 'this' address
            var thisAddressFromInterfaceResultNumber = this.WriteSetResultNumber(opCodeMethodInfo, thisType);
            writer.Write("getelementptr ");
            this.WriteMethodPointerType(writer, methodInfo);
            writer.Write("** ");
            this.WriteResultNumber(pointerToInterfaceVirtualTablePointersResultNumber);
            writer.Write(", i32 ");
            this.WriteResultNumber(offsetAddressAsIntResultNumber);
            writer.WriteLine(string.Empty);

            // adjust 'this' pointer
            this.WriteSetResultNumber(opCodeMethodInfo, thisType);
            writer.Write("bitcast ");
            this.WriteMethodPointerType(writer, methodInfo);
            writer.Write("** ");
            this.WriteResultNumber(thisAddressFromInterfaceResultNumber);
            writer.Write(" to ");
            thisType.WriteTypePrefix(writer);
            writer.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="declaringType">
        /// </param>
        /// <param name="interface">
        /// </param>
        public void WriteInterfaceAccess(LlvmIndentedTextWriter writer, OpCodePart opCode, IType declaringType, IType @interface)
        {
            var objectResult = opCode.Result;

            writer.WriteLine("; Get interface '{0}' of '{1}'", @interface, declaringType);

            declaringType.UseAsClass = true;

            this.ProcessOperator(
                writer,
                opCode,
                "getelementptr inbounds",
                declaringType,
                @interface,
                OperandOptions.TypeIsInOperator | OperandOptions.GenerateResult);
            writer.Write(' ');
            writer.Write(objectResult);

            this.WriteInterfaceIndex(writer, declaringType, @interface);

            writer.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="methodBase">
        /// </param>
        /// <param name="ownerOfExplicitInterface">
        /// </param>
        public void WriteMethodDefinitionName(LlvmIndentedTextWriter writer, IMethod methodBase, IType ownerOfExplicitInterface = null)
        {
            writer.Write(this.GetFullMethodName(methodBase, ownerOfExplicitInterface));
        }

        /// <summary>
        /// </summary>
        /// <param name="method">
        /// </param>
        public void WriteMethodEnd(IMethod method, bool doNotWrite = false)
        {
            this.WriteMethodBody(doNotWrite);

            if (doNotWrite)
            {
                return;
            }

            if (!this.NoBody)
            {
                if (this.needToWriteUnwindException)
                {
                    this.needToWriteUnwindException = false;
                    this.WriteUnwindException();
                }

                if (this.needToWriteUnreachable)
                {
                    this.needToWriteUnreachable = false;
                    this.WriteUnreachable();
                }

                if (method.ExceptionHandlingClauses.Any())
                {
                    this.WriteResume();
                }

                this.Output.EndMethodBody();

                this.Output.Indent--;
                this.Output.WriteLine("}");
            }

            this.Output.WriteLine(string.Empty);

            // write set of strings
            foreach (var pair in this.stringStorage)
            {
                WriteUnicodeString(pair);
            }

            if (this.stringStorage.Count > 0)
            {
                this.Output.WriteLine(string.Empty);
            }

            this.stringStorage.Clear();
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="parameterInfos">
        /// </param>
        /// <param name="hasThis">
        /// </param>
        /// <param name="thisType">
        /// </param>
        /// <param name="returnType">
        /// </param>
        /// <param name="noArgumentName">
        /// </param>
        public void WriteMethodParamsDef(
            LlvmIndentedTextWriter writer, IEnumerable<IParameter> parameterInfos, bool hasThis, IType thisType, IType returnType, bool noArgumentName = false)
        {
            writer.Write("(");

            var hasParameterWritten = false;

            if (returnType.IsStructureType())
            {
                returnType.WriteTypePrefix(writer, returnType.IsStructureType());
                hasParameterWritten = true;

                if (!noArgumentName)
                {
                    writer.Write(" noalias sret %agg.result");
                }
            }

            var start = hasThis ? 1 : 0;

            if (hasThis)
            {
                if (returnType.IsStructureType())
                {
                    writer.Write(", ");
                }

                thisType.UseAsClass = true;

                thisType.WriteTypePrefix(writer, true);
                hasParameterWritten = true;

                if (!noArgumentName)
                {
                    writer.Write(" %this");
                }
            }

            var index = start;
            foreach (var parameter in parameterInfos)
            {
                this.CheckIfExternalDeclarationIsRequired(parameter.ParameterType);

                if (hasParameterWritten)
                {
                    writer.Write(", ");
                }

                parameter.ParameterType.WriteTypePrefix(writer, parameter.ParameterType.IsStructureType());
                hasParameterWritten = true;

                if (parameter.ParameterType.IsStructureType())
                {
                    if (!noArgumentName)
                    {
                        writer.Write(" byval align " + PointerSize);
                        writer.Write(" %.");
                    }
                }
                else if (!noArgumentName)
                {
                    writer.Write(" %");
                }

                if (!noArgumentName)
                {
                    writer.Write(parameter.Name);
                }

                index++;
            }

            writer.Write(")");
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="methodBase">
        /// </param>
        public void WriteMethodPointerType(LlvmIndentedTextWriter writer, IMethod methodBase)
        {
            var methodInfo = methodBase;
            methodInfo.ReturnType.WriteTypePrefix(writer);

            writer.Write(" (");

            var hasThis = !methodInfo.IsStatic;

            if (hasThis)
            {
                methodInfo.DeclaringType.WriteTypePrefix(writer);
            }

            var index = 0;
            foreach (var parameter in methodBase.GetParameters())
            {
                if (index > 0 || hasThis)
                {
                    writer.Write(", ");
                }

                parameter.ParameterType.WriteTypePrefix(writer);
                index++;
            }

            writer.Write(")*");
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="method">
        /// </param>
        public void WriteMethodReturnType(LlvmIndentedTextWriter writer, IMethod method)
        {
            this.CheckIfExternalDeclarationIsRequired(method.ReturnType);

            if (!method.ReturnType.IsVoid() && !method.ReturnType.IsStructureType())
            {
                method.ReturnType.WriteTypePrefix(writer, false);
                writer.Write(" ");
            }
            else
            {
                this.Output.Write("void ");
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="method">
        /// </param>
        public void WriteMethodStart(IMethod method, bool doNotWrite = false)
        {
            if (method.IsInternalCall && this.processedMethods.Any(m => m.Name == method.Name))
            {
                return;
            }

            this.StartProcess();

            if (doNotWrite)
            {
                this.ReadMethodInfo(method);
                return;
            }

            this.WriteRequiredTypesForBody();

            this.processedMethods.Add(method);

            this.ReadMethodInfo(method);

            var isMain = method.IsStatic && method.CallingConvention.HasFlag(CallingConventions.Standard) && method.Name.Equals("Main");

            // check if main
            if (isMain)
            {
                this.MainMethod = method;
            }

            if (method.IsGenericMethod)
            {
                ////this.Output.Write("template <");
                ////WriteGenericParameters(this.Output, method);
                ////this.Output.Write("> ");
            }

            if (method.IsAbstract)
            {
                return;
            }

            var isDelegateBodyFunctions = method.IsDelegateFunctionBody();
            if ((method.IsAbstract || method.GetMethodBody() == null) && !isDelegateBodyFunctions)
            {
                this.Output.Write("declare ");
            }
            else
            {
                this.Output.Write("define ");
            }

            this.WriteMethodReturnType(this.Output, method);

            this.WriteMethodDefinitionName(this.Output, method);
            if (method.IsExternalLibraryMethod())
            {
                this.Output.Write("(...)");
            }
            else
            {
                this.WriteMethodParamsDef(this.Output, method.GetParameters(), this.HasMethodThis, this.ThisType, method.ReturnType);
            }

            this.WriteMethodNumber();

            // write local declarations
            var methodBodyBytes = method.GetMethodBody();
            if (methodBodyBytes != null)
            {
                this.Output.WriteLine(" {");

                this.Output.Indent++;

                this.WriteLocalVariableDeclarations(methodBodyBytes.LocalVariables);
                this.WriteArgumentCopyDeclarations(method.GetParameters(), this.HasMethodThis);

                this.Output.StartMethodBody();
            }
            else
            {
                if (isDelegateBodyFunctions)
                {
                    this.WriteDelegateFunctionBody(method);
                }

                this.Output.WriteLine(string.Empty);
            }

            methodNumberIncremental++;
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        public void WriteResultNumber(OpCodePart opCode)
        {
            // write number of method
            this.Output.Write(opCode.Result);
        }

        /// <summary>
        /// </summary>
        /// <param name="result">
        /// </param>
        public void WriteResultNumber(LlvmResult result)
        {
            // write number of method
            this.Output.Write(result);
        }

        public void WriteResultNumber(FullyDefinedReference reference)
        {
            // write number of method
            this.Output.Write(reference.Name);
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        /// <param name="type">
        /// </param>
        /// <returns>
        /// </returns>
        public LlvmResult WriteSetResultNumber(OpCodePart opCode, IType type)
        {
            var writer = this.Output;

            // write number of method
            writer.Write("%.r");
            writer.Write(++this.resultNumberIncremental);
            writer.Write(" = ");

            var llvmResult = new LlvmResult(this.resultNumberIncremental, type);
            if (opCode != null)
            {
                opCode.Result = llvmResult;
            }

            return llvmResult;
        }

        public int GetBlockJumpAddress()
        {
            return ++this.blockJumpAddressIncremental;
        }

        /// <summary>
        /// </summary>
        /// <param name="moduleName">
        /// </param>
        public void WriteStart(string moduleName, string assemblyName)
        {
            AssemblyQualifiedName = assemblyName;

            this.Output.WriteLine(
                "target datalayout = \"e-p:32:32:32-i1:8:8-i8:8:8-i16:16:16-i32:32:32-i64:64:64-f32:32:32-f64:64:64-f80:128:128-v64:64:64-v128:128:128-a0:0:64-f80:32:32-n8:16:32-S32\"");
            this.Output.WriteLine("target triple = \"i686-pc-win32\"");
            this.Output.WriteLine(string.Empty);

            // Global ctors
            this.Output.WriteLine(
                "@llvm.global_ctors = appending global [1 x { i32, void ()* }] [{ i32, void ()* } { i32 65535, void ()* @_GLOBAL_CTORS_EXECUTE_ }]");
            this.Output.WriteLine(string.Empty);

            // declarations
            this.Output.WriteLine(new String(Encoding.ASCII.GetChars(Resources.llvm_declarations)));
            this.Output.WriteLine(string.Empty);

            if (this.IncludeMiniCoreLib)
            {
                // mini core lib
                this.Output.WriteLine(new String(Encoding.ASCII.GetChars(Resources.llvm_mini_mscore_lib)));
                this.Output.WriteLine(string.Empty);
            }

            this.StaticConstructors.Clear();
            VirtualTableGen.Clear();
            TypeGen.Clear();
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        public void WriteTypeEnd(IType type)
        {
            this.Output.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        /// <param name="genericType">
        /// </param>
        public void WriteTypeStart(IType type, IType genericType)
        {
            this.processedTypes.Add(type);

            // get all required types for type definition
            var requiredTypes = new List<IType>();
            Il2Converter.ProcessRequiredITypesForITypes(new[] { type }, new HashSet<IType>(), requiredTypes, null);
            foreach (var requiredType in requiredTypes)
            {
                this.WriteTypeDefinitionIfNotWrittenYet(requiredType);
            }

            var interfacesList = type.GetInterfaces();
            foreach (var @interface in interfacesList.Where(@interface => type.BaseType == null || !type.BaseType.GetInterfaces().Contains(@interface)))
            {
                this.WriteTypeDefinitionIfNotWrittenYet(@interface);
            }

            this.staticFieldsInfo.Clear();

            this.ReadTypeInfo(type, genericType);

            if (type.IsGenericType)
            {
                this.Output.Write("template <");
                this.WriteGenericParameters(this.Output, type);
                this.Output.Write("> ");
            }

            this.WriteTypeDeclarationStart(type);
        }

        /// <summary>
        /// </summary>
        /// <param name="field">
        /// </param>
        /// <returns>
        /// </returns>
        private static string GetFullFieldName(IField field)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(field.DeclaringType.Namespace))
            {
                sb.Append(field.DeclaringType.Namespace);
                sb.Append('.');
            }

            sb.Append(field.DeclaringType.Name);
            sb.Append('.');
            sb.Append(field.Name);

            return sb.ToString();
        }

        /// <summary>
        /// </summary>
        /// <param name="isFloatingPoint">
        /// </param>
        /// <returns>
        /// </returns>
        private static OperandOptions GetOperandOptions(bool isFloatingPoint)
        {
            var operandOptions = OperandOptions.GenerateResult;
            if (isFloatingPoint)
            {
                operandOptions |= OperandOptions.ToFloat;
            }

            return operandOptions;
        }

        /// <summary>
        /// if true - suppress ; at the end of line
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="firstLevel">
        /// </param>
        public void ActualWrite(LlvmIndentedTextWriter writer, OpCodePart opCode, bool firstLevel = false)
        {
            if (firstLevel)
            {
                this.WriteCaseAndLabels(writer, opCode);
            }

            if (opCode.Any(Code.Leave, Code.Leave_S))
            {
                this.WriteCatchFinnallyEnd(writer, opCode);
            }

            this.WriteTryBegins(writer, opCode);
            this.WriteCatchBegins(writer, opCode);

            var block = opCode as OpCodeBlock;
            if (block != null)
            {
                this.ActualWriteBlock(writer, block);
            }
            else
            {
                var skip = firstLevel && (this.IsDirectValue(opCode) || opCode.Skip);
                if (!skip)
                {
                    this.ActualWriteOpCode(writer, opCode);
                }
            }

            if (!opCode.Any(Code.Leave, Code.Leave_S))
            {
                this.WriteCatchFinnallyEnd(writer, opCode);
            }

            this.WriteCatchFinnallyCleanUpEnd(opCode);
            this.WriteTryEnds(writer, opCode);
            this.WriteExceptionHandlers(writer, opCode);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="block">
        /// </param>
        private void ActualWriteBlock(LlvmIndentedTextWriter writer, OpCodeBlock block)
        {
            if (block.UseAsConditionalExpression)
            {
                var usePhi = block.OpCodes.Length > 4;

                // thos os a hack for return () ? a : b; expressions
                var expressionPart = -1;
                if (block.OpCodes[block.OpCodes.Length - 2].OpCode.FlowControl == FlowControl.Branch)
                {
                    expressionPart = 3;
                }
                else if (block.OpCodes[block.OpCodes.Length - 2].OpCode.FlowControl == FlowControl.Return)
                {
                    expressionPart = 2;
                }

                // to support PHI                          
                if (usePhi)
                {
                    writer.WriteLine("br label %.a{0}", block.OpCodes[0].GroupAddressStart);
                    writer.Indent--;
                    writer.WriteLine(".a{0}:", block.OpCodes[0].GroupAddressStart);
                    writer.Indent++;
                }

                var lastCond = block.OpCodes.Length - expressionPart;
                for (var i = 0; i < lastCond - 1; i++)
                {
                    var current = block.OpCodes[i];

                    if (usePhi)
                    {
                        current.CustomJumpAddress = block.OpCodes[lastCond].GroupAddressStart;
                    }

                    this.ActualWrite(writer, current);

                    if (usePhi)
                    {
                        current.CustomJumpAddress = null;
                    }
                }

                var opCode1 = block.OpCodes[lastCond - 1];
                opCode1.UseAsConditionalExpression = true;
                var opCode2 = block.OpCodes[block.OpCodes.Length - 1];
                var opCode3 = (expressionPart == 2)
                                  ? block.OpCodes[block.OpCodes.Length - expressionPart].OpCodeOperands[0]
                                  : block.OpCodes[block.OpCodes.Length - expressionPart];

                // custom operand
                var directResult1 = this.PreProcess(writer, opCode1, OperandOptions.None);

                // check if PHI is required
                if (usePhi)
                {
                    writer.WriteLine("br label %.a{0}", block.OpCodes[lastCond].GroupAddressStart);
                    writer.Indent--;
                    writer.WriteLine(".a{0}:", block.OpCodes[lastCond].GroupAddressStart);
                    writer.Indent++;

                    // apply PHI is condition is complex
                    this.ProcessOperator(writer, block, "phi", this.ResolveType("System.Boolean"), options: OperandOptions.GenerateResult);
                    var phiResult = block.Result;

                    // write labels
                    for (var i = lastCond - 2; i >= 0; i--)
                    {
                        if (i != (lastCond - 2))
                        {
                            writer.Write(",");
                        }

                        // true. false, %result
                        var phiValue = block.OpCodes[i].JumpAddress() == opCode2.GroupAddressStart ? "true" : "false";
                        writer.Write(" [ {0}, %.a{1} ]", phiValue, block.OpCodes[i].GroupAddressStart);
                    }

                    writer.WriteLine(", [ {0}, %.a{1} ]", block.OpCodes[lastCond - 1].Result, block.OpCodes[lastCond - 1].GroupAddressStart);

                    // hack
                    block.OpCodes[lastCond - 1].Result = block.Result;
                }

                var directResult2 = this.PreProcess(writer, opCode2, OperandOptions.None);
                var directResult3 = this.PreProcess(writer, opCode3, OperandOptions.None);

                IType castFrom;
                var effectiveType = this.DetectTypePrefix(opCode2, opCode3, out castFrom);

                this.ProcessOperator(writer, block, "select", this.ResolveType("System.Boolean"), options: OperandOptions.GenerateResult);

                this.PostProcess(writer, opCode1, directResult1);
                writer.Write(',');
                this.PostProcess(writer, opCode2, directResult2, effectiveType == null, effectiveType);
                writer.Write(',');
                this.PostProcess(writer, opCode3, directResult3, effectiveType == null, effectiveType);

                block.Result = block.Result;

                return;
            }

            // just array
            this.ActualWriteBlockBody(writer, block);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="block">
        /// </param>
        /// <param name="skip">
        /// </param>
        /// <param name="reduce">
        /// </param>
        private void ActualWriteBlockBody(LlvmIndentedTextWriter writer, OpCodeBlock block, int skip = 0, int? reduce = null)
        {
            var query = reduce.HasValue ? block.OpCodes.Take(block.OpCodes.Length - reduce.Value).Skip(skip) : block.OpCodes.Skip(skip);

            foreach (var subOpCode in query)
            {
                this.ActualWrite(writer, subOpCode);
            }
        }

        private string GetDirectName(OpCodePart opCodePart)
        {
            var output = this.Output;

            var sb = new StringBuilder();
            this.Output = new LlvmIndentedTextWriter(new StringWriter(sb));

            this.ActualWrite(this.Output, opCodePart);

            this.Output = output;
            return sb.ToString();
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void ActualWriteOpCode(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            var code = opCode.ToCode();
            switch (code)
            {
                case Code.Ldc_I4_0:
                    writer.Write(opCode.UseAsBoolean ? "false" : opCode.UseAsNull ? "null" : "0");
                    break;
                case Code.Ldc_I4_1:
                    writer.Write(opCode.UseAsBoolean ? "true" : "1");
                    break;
                case Code.Ldc_I4_2:
                    writer.Write("2");
                    break;
                case Code.Ldc_I4_3:
                    writer.Write("3");
                    break;
                case Code.Ldc_I4_4:
                    writer.Write("4");
                    break;
                case Code.Ldc_I4_5:
                    writer.Write("5");
                    break;
                case Code.Ldc_I4_6:
                    writer.Write("6");
                    break;
                case Code.Ldc_I4_7:
                    writer.Write("7");
                    break;
                case Code.Ldc_I4_8:
                    writer.Write("8");
                    break;
                case Code.Ldc_I4_M1:
                    writer.Write("-1");
                    break;
                case Code.Ldc_I4:
                    var opCodeInt32 = opCode as OpCodeInt32Part;
                    writer.Write(opCodeInt32.Operand);
                    break;
                case Code.Ldc_I4_S:
                    opCodeInt32 = opCode as OpCodeInt32Part;

                    if (opCodeInt32.Operand > 127)
                    {
                        // negative
                        writer.Write(-(256 - opCodeInt32.Operand));
                    }
                    else
                    {
                        writer.Write(opCodeInt32.Operand);
                    }

                    break;
                case Code.Ldc_I8:
                    var opCodeInt64 = opCode as OpCodeInt64Part;
                    writer.Write(opCodeInt64.Operand);
                    break;
                case Code.Ldc_R4:
                    var opCodeSingle = opCode as OpCodeSinglePart;

                    if (opCodeSingle.Operand == float.PositiveInfinity)
                    {
                        writer.Write("0x7FF00000");
                    }
                    else if (opCodeSingle.Operand == float.NegativeInfinity)
                    {
                        writer.Write("0xFFF00000");
                    }
                    else
                    {
                        writer.Write(opCodeSingle.Operand.ToString("F"));
                    }

                    break;
                case Code.Ldc_R8:
                    var opCodeDouble = opCode as OpCodeDoublePart;

                    if (opCodeDouble.Operand == double.PositiveInfinity)
                    {
                        writer.Write("0x7FF0000000000000");
                    }
                    else if (opCodeDouble.Operand == double.NegativeInfinity)
                    {
                        writer.Write("0xFFF0000000000000");
                    }
                    else
                    {
                        writer.Write(opCodeDouble.Operand.ToString("F"));
                    }

                    break;
                case Code.Ldstr:
                    var opCodeString = opCode as OpCodeStringPart;
                    if (opCodeString.StringIndex == 0)
                    {
                        var stringType = this.ResolveType("System.String");

                        // find constructor
                        var constructorInfo = IlReader.Constructors(stringType).First(
                            c => c.GetParameters().Count() == 1
                                    && c.GetParameters().First().ParameterType.ToString() == "Char[]");

                        this.WriteNewWithoutCallingConstructor(opCode, stringType);
                        opCodeString.StringIndex = this.GetStringIndex(opCodeString.Operand);
                        opCode.OpCodeOperands = new OpCodePart[] { opCode };
                        this.WriteCallConstructor(opCode, constructorInfo);
                        opCodeString.StringIndex = 0;
                    }
                    else
                    {
                        writer.Write("bitcast ([{1} x i16]* getelementptr inbounds ({2} i32, [{1} x i16] {3}* @.s{0}, i32 0, i32 1) to i16*)", opCodeString.StringIndex, opCodeString.Operand.Length + 1, '{', '}');
                    }

                    break;
                case Code.Ldnull:
                    writer.Write("null");
                    break;
                case Code.Ldtoken:

                    // TODO: finish loading Token
                    opCodeInt32 = opCode as OpCodeInt32Part;
                    writer.Write("undef");
                    break;
                case Code.Localloc:
                    writer.Write("alloca i32 ");
                    this.ActualWrite(writer, opCode.OpCodeOperands[0]);
                    writer.Write(", align " + PointerSize);
                    break;
                case Code.Ldfld:

                    var opCodeFieldInfoPart = opCode as OpCodeFieldInfoPart;

                    // we wait when opCode.DestinationName is set;
                    var skip = opCodeFieldInfoPart.Operand.FieldType.IsStructureType() && opCode.DestinationName == null;
                    if (!skip)
                    {
                        this.WriteFieldAccess(writer, opCodeFieldInfoPart);
                        writer.WriteLine(string.Empty);

                        var memberAccessResultNumber = opCode.Result;
                        opCode.Result = null;
                        this.WriteLlvmLoad(opCode, memberAccessResultNumber.Type, memberAccessResultNumber);
                    }

                    break;
                case Code.Ldflda:

                    opCodeFieldInfoPart = opCode as OpCodeFieldInfoPart;
                    this.WriteFieldAccess(writer, opCodeFieldInfoPart);

                    break;
                case Code.Ldsfld:

                    IType castFrom;
                    IType intAdjustment;
                    bool intAdjustSecondOperand;
                    var operandType = this.DetectTypePrefix(opCode, null, OperandOptions.TypeIsInOperator, out castFrom, out intAdjustment, out intAdjustSecondOperand);
                    opCodeFieldInfoPart = opCode as OpCodeFieldInfoPart;

                    var destinationName = string.Concat("@\"", GetFullFieldName(opCodeFieldInfoPart.Operand), '"');
                    if (operandType.IsStructureType())
                    {
                        writer.Write(destinationName);
                    }
                    else
                    {
                        this.WriteLlvmLoad(opCode, operandType, new FullyDefinedReference(destinationName, opCodeFieldInfoPart.Operand.FieldType));
                    }

                    break;
                case Code.Ldsflda:

                    opCodeFieldInfoPart = opCode as OpCodeFieldInfoPart;
                    writer.Write(string.Concat("@\"", GetFullFieldName(opCodeFieldInfoPart.Operand), '"'));

                    break;
                case Code.Stfld:

                    this.WriteGetFieldAccessAndSaveToField(opCode as OpCodeFieldInfoPart);

                    break;
                case Code.Stsfld:

                    opCodeFieldInfoPart = opCode as OpCodeFieldInfoPart;

                    var directResult1 = this.PreProcessOperand(writer, opCode, 0);

                    destinationName = string.Concat("@\"", GetFullFieldName(opCodeFieldInfoPart.Operand), '"');
                    operandType = opCodeFieldInfoPart.Operand.FieldType;

                    if (opCodeFieldInfoPart.Operand.FieldType.IsStructureType())
                    {
                        opCode.DestinationName = destinationName;
                        if (IsDirectValue(opCode.OpCodeOperands[0]))
                        {
                            this.WriteLlvmLoad(opCode, operandType, new FullyDefinedReference(GetDirectName(opCode.OpCodeOperands[0]), opCodeFieldInfoPart.Operand.FieldType));
                        }
                        else
                        {
                            this.WriteLlvmLoad(opCode, operandType, opCode.OpCodeOperands[0].Result);
                        }
                    }
                    else
                    {
                        this.ProcessOperator(writer, opCode, "store", operandType, options: OperandOptions.TypeIsInSecondOperand | OperandOptions.AdjustIntTypes);
                        this.PostProcessOperand(writer, opCode, 0, directResult1);
                        writer.Write(", ");
                        operandType.WriteTypePrefix(writer);
                        writer.Write("* ");
                        writer.Write(destinationName);
                    }

                    break;

                case Code.Ldobj:

                    var opCodeTypePart = opCode as OpCodeTypePart;

                    directResult1 = this.PreProcessOperand(writer, opCode, 0);

                    if (opCode.DestinationName != null || !opCode.OpCodeOperands[0].Result.Type.UseAsClass)
                    {
                        this.WriteLlvmLoad(opCode, opCodeTypePart.Operand, opCode.OpCodeOperands[0].Result);
                    }
                    else
                    {
                        opCode.Result = opCode.OpCodeOperands[0].Result;
                    }

                    break;

                case Code.Stobj:
                    opCodeTypePart = opCode as OpCodeTypePart;

                    this.ActualWrite(writer, opCode.OpCodeOperands[0]);
                    writer.WriteLine(string.Empty);

                    if (!opCode.OpCodeOperands[1].HasResult)
                    {
                        // we expect to see Ldobj here, so we set DestinationName to copy it into reserved stack
                        opCode.OpCodeOperands[1].DestinationName = opCode.OpCodeOperands[0].Result.ToString();
                        this.ActualWrite(writer, opCode.OpCodeOperands[1]);
                    }
                    else
                    {
                        directResult1 = this.PreProcessOperand(writer, opCode, 0);

                        this.UnaryOper(writer, opCode, 1, "store", opCodeTypePart.Operand);
                        writer.Write(", ");
                        opCode.OpCodeOperands[0].Result.Type.WriteTypePrefix(writer);
                        writer.Write("* ");
                        this.PostProcessOperand(writer, opCode, 0, directResult1);
                    }

                    break;
                case Code.Ldlen:
                    this.WriteArrayGetLength(opCode);
                    break;
                case Code.Ldelem:
                case Code.Ldelem_I:
                case Code.Ldelem_I1:
                case Code.Ldelem_I2:
                case Code.Ldelem_I4:
                case Code.Ldelem_I8:
                case Code.Ldelem_R4:
                case Code.Ldelem_R8:
                case Code.Ldelem_Ref:
                case Code.Ldelem_U1:
                case Code.Ldelem_U2:
                case Code.Ldelem_U4:
                case Code.Ldelema:

                    LoadElement(writer, opCode);
                    break;

                case Code.Stelem:
                case Code.Stelem_I:
                case Code.Stelem_I1:
                case Code.Stelem_I2:
                case Code.Stelem_I4:
                case Code.Stelem_I8:
                case Code.Stelem_R4:
                case Code.Stelem_R8:
                case Code.Stelem_Ref:

                    SaveElement(writer, opCode);
                    break;

                case Code.Ldind_I:
                case Code.Ldind_I1:
                case Code.Ldind_I2:
                case Code.Ldind_I4:
                case Code.Ldind_I8:
                case Code.Ldind_R4:
                case Code.Ldind_R8:
                case Code.Ldind_Ref:
                case Code.Ldind_U1:
                case Code.Ldind_U2:
                case Code.Ldind_U4:

                    LoadIndirect(writer, opCode);
                    break;

                case Code.Stind_I:
                case Code.Stind_I1:
                case Code.Stind_I2:
                case Code.Stind_I4:
                case Code.Stind_I8:
                case Code.Stind_R4:
                case Code.Stind_R8:
                case Code.Stind_Ref:

                    SaveIndirect(writer, opCode);
                    break;

                case Code.Call:
                case Code.Callvirt:
                    var opCodeMethodInfoPart = opCode as OpCodeMethodInfoPart;
                    var methodBase = opCodeMethodInfoPart.Operand;
                    this.WriteCall(
                        opCodeMethodInfoPart,
                        methodBase,
                        code == Code.Callvirt,
                        methodBase.CallingConvention.HasFlag(CallingConventions.HasThis),
                        false,
                        null,
                        this.tryScopes.Count > 0 ? this.tryScopes.Peek() : null);
                    break;
                case Code.Add:
                case Code.Add_Ovf:
                case Code.Add_Ovf_Un:
                    var isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fadd" : "add", GetOperandOptions(isFloatingPoint) | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Mul:
                case Code.Mul_Ovf:
                case Code.Mul_Ovf_Un:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fmul" : "mul", GetOperandOptions(isFloatingPoint) | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Sub:
                case Code.Sub_Ovf:
                case Code.Sub_Ovf_Un:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fsub" : "sub", GetOperandOptions(isFloatingPoint) | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Div:
                case Code.Div_Un:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fdiv" : "sdiv", GetOperandOptions(isFloatingPoint) | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Rem:
                case Code.Rem_Un:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "frem" : "srem", GetOperandOptions(isFloatingPoint) | OperandOptions.AdjustIntTypes);
                    break;
                case Code.And:
                    this.BinaryOper(writer, opCode, "and", OperandOptions.AdjustIntTypes);
                    break;
                case Code.Or:
                    this.BinaryOper(writer, opCode, "or", OperandOptions.AdjustIntTypes);
                    break;
                case Code.Xor:
                    this.BinaryOper(writer, opCode, "xor", OperandOptions.AdjustIntTypes);
                    break;
                case Code.Shl:
                    this.BinaryOper(writer, opCode, "shl", OperandOptions.AdjustIntTypes);
                    break;
                case Code.Shr:
                case Code.Shr_Un:
                    this.BinaryOper(writer, opCode, "lshr", OperandOptions.AdjustIntTypes);
                    break;
                case Code.Not:
                    var tempOper = opCode.OpCodeOperands;
                    opCode.OpCodeOperands = new[] { tempOper[0], new OpCodePart(OpCodesEmit.Ldc_I4_M1, 0, 0) };
                    this.BinaryOper(writer, opCode, "xor");
                    opCode.OpCodeOperands = tempOper;
                    break;
                case Code.Neg:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    tempOper = opCode.OpCodeOperands;
                    opCode.OpCodeOperands = new[] 
                    { 
                        isFloatingPoint 
                            ? new OpCodeDoublePart(OpCodesEmit.Ldc_R8, 0, 0, 0.0) 
                            : GetTypedIntZeroCode(opCode.OpCodeOperands[0].Result.Type), 
                        tempOper[0] 
                    };

                    this.BinaryOper(
                        writer,
                        opCode,
                        isFloatingPoint ? "fsub" : "sub",
                        options: GetOperandOptions(isFloatingPoint) | OperandOptions.TypeIsInSecondOperand | OperandOptions.AdjustIntTypes);
                    opCode.OpCodeOperands = tempOper;
                    break;
                case Code.Dup:

                    if (opCode.DestinationName != null)
                    {
                        opCode.OpCodeOperands[0].DestinationName = opCode.DestinationName;
                    }

                    this.ActualWrite(writer, opCode.OpCodeOperands[0]);
                    break;
                case Code.Box:

                    var type = (opCode as OpCodeTypePart).Operand;
                    type.WriteCallBoxObjectMethod(this, opCode);

                    break;

                case Code.Unbox:
                case Code.Unbox_Any:

                    type = (opCode as OpCodeTypePart).Operand;
                    type.WriteCallUnboxObjectMethod(this, opCode);

                    break;
                case Code.Ret:

                    var opts = OperandOptions.None;

                    if (this.MethodReturnType.IsStructureType())
                    {
                        var operands = opCode.OpCodeOperands;
                        var opCodeOperand = operands[0];
                        opCodeOperand.DestinationName = "%agg.result";
                        opCodeOperand.DestinationType = this.MethodReturnType;

                        opts |= OperandOptions.IgnoreOperand;
                    }

                    this.UnaryOper(writer, opCode, "ret", this.MethodReturnType, options: opts | OperandOptions.AdjustIntTypes);

                    if (this.MethodReturnType.IsStructureType())
                    {
                        writer.Write("void");
                    }

                    break;
                case Code.Stloc:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                case Code.Stloc_S:

                    code = opCode.ToCode();
                    var asString = code.ToString();
                    var index = 0;
                    if (code == Code.Stloc_S || code == Code.Stloc)
                    {
                        index = (opCode as OpCodeInt32Part).Operand;
                    }
                    else
                    {
                        index = int.Parse(asString.Substring(asString.Length - 1));
                    }

                    var localType = this.LocalInfo[index].LocalType;

                    if (localType.IsStructureType())
                    {
                        opCode.OpCodeOperands[0].DestinationName = this.GetLocalVarName(index);
                        this.ActualWrite(writer, opCode.OpCodeOperands[0]);
                    }
                    else
                    {
                        this.UnaryOper(writer, opCode, "store", localType, options: OperandOptions.AdjustIntTypes);
                        writer.Write(", ");
                        this.WriteLlvmLocalVarAccess(index, true);
                    }

                    break;
                case Code.Ldloc:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                case Code.Ldloc_S:
                    asString = code.ToString();

                    if (opCode.Any(Code.Ldloc_S, Code.Ldloc))
                    {
                        index = (opCode as OpCodeInt32Part).Operand;
                    }
                    else
                    {
                        index = int.Parse(asString.Substring(asString.Length - 1));
                    }

                    destinationName = string.Concat("%local", index);

                    localType = this.LocalInfo[index].LocalType;

                    skip = this.LocalInfo[index].LocalType.IsStructureType() && opCode.DestinationName == null;
                    if (!skip)
                    {
                        this.WriteLlvmLoad(opCode, new FullyDefinedReference(destinationName, localType));
                    }
                    else
                    {
                        writer.Write(destinationName);
                    }

                    break;
                case Code.Ldloca:
                case Code.Ldloca_S:

                    opCodeInt32 = opCode as OpCodeInt32Part;
                    index = opCodeInt32.Operand;
                    // alloca generate pointer so we do not need to load value from pointer
                    localType = this.LocalInfo[index].LocalType;
                    if (localType.IsPointer)
                    {
                        this.WriteLlvmLoad(opCode, new FullyDefinedReference(string.Concat("%local", index), localType));
                    }
                    else
                    {
                        writer.Write(string.Concat("%local", index));
                    }

                    break;
                case Code.Ldarg:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                case Code.Ldarg_S:
                    asString = code.ToString();
                    if (opCode.Any(Code.Ldarg_S, Code.Ldarg))
                    {
                        opCodeInt32 = opCode as OpCodeInt32Part;
                        index = opCodeInt32.Operand;
                    }
                    else
                    {
                        index = int.Parse(asString.Substring(asString.Length - 1));
                    }

                    if (this.HasMethodThis && index == 0)
                    {
                        this.ThisType.UseAsClass = true;
                        this.WriteLlvmLoad(opCode, new FullyDefinedReference("%.this", this.ThisType), true, this.ThisType.IsStructureType());
                    }
                    else
                    {
                        var parameter = this.Parameters[index - (this.HasMethodThis ? 1 : 0)];

                        destinationName = string.Concat("%.", parameter.Name);

                        skip = parameter.ParameterType.IsStructureType() && opCode.DestinationName == null;
                        if (!skip)
                        {
                            this.WriteLlvmLoad(opCode, new FullyDefinedReference(destinationName, parameter.ParameterType));
                        }
                        else
                        {
                            writer.Write(destinationName);
                        }
                    }

                    break;

                case Code.Ldarga:
                case Code.Ldarga_S:

                    opCodeInt32 = opCode as OpCodeInt32Part;
                    index = opCodeInt32.Operand;

                    if (this.HasMethodThis && index == 0)
                    {
                        writer.Write("%.this");
                    }
                    else
                    {
                        var parameter = this.Parameters[index - (this.HasMethodThis ? 1 : 0)];
                        if (parameter.ParameterType.IsPointer)
                        {
                            this.WriteLlvmLoad(opCode, new FullyDefinedReference(string.Concat("%.", parameter.Name), parameter.ParameterType));
                        }
                        else
                        {
                            writer.Write(string.Concat("%.", parameter.Name));
                        }
                    }

                    break;

                case Code.Starg:
                case Code.Starg_S:

                    opCodeInt32 = opCode as OpCodeInt32Part;
                    index = opCodeInt32.Operand;
                    var actualIndex = index - (this.HasMethodThis ? 1 : 0);
                    this.UnaryOper(writer, opCode, "store", this.Parameters[actualIndex].ParameterType, options: OperandOptions.AdjustIntTypes);
                    writer.Write(", ");
                    this.WriteLlvmArgVarAccess(writer, index - (this.HasMethodThis ? 1 : 0), true);

                    break;

                case Code.Ldftn:

                    // bitcast (i8* (i8*)* @"[I]System.String fn(System.String)" to i32*)
                    opCodeMethodInfoPart = opCode as OpCodeMethodInfoPart;

                    writer.Write("bitcast (");
                    this.WriteMethodPointerType(writer, opCodeMethodInfoPart.Operand);
                    writer.Write(" ");
                    writer.Write(this.GetFullMethodName(opCodeMethodInfoPart.Operand));
                    writer.Write(" to i32*)");

                    break;

                case Code.Beq:
                case Code.Beq_S:
                case Code.Bne_Un:
                case Code.Bne_Un_S:
                case Code.Blt:
                case Code.Blt_S:
                case Code.Blt_Un:
                case Code.Blt_Un_S:
                case Code.Ble:
                case Code.Ble_S:
                case Code.Ble_Un:
                case Code.Ble_Un_S:
                case Code.Bgt:
                case Code.Bgt_S:
                case Code.Bgt_Un:
                case Code.Bgt_Un_S:
                case Code.Bge:
                case Code.Bge_S:
                case Code.Bge_Un:
                case Code.Bge_Un_S:

                    // we need to invert all comare command
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    var oper = string.Empty;
                    switch (opCode.ToCode())
                    {
                        case Code.Beq:
                        case Code.Beq_S:
                            oper = isFloatingPoint ? "fcmp oeq" : "icmp eq";
                            break;
                        case Code.Bne_Un:
                        case Code.Bne_Un_S:
                            oper = isFloatingPoint ? "fcmp one" : "icmp ne";
                            break;
                        case Code.Blt:
                        case Code.Blt_S:
                            oper = isFloatingPoint ? "fcmp olt" : "icmp slt";
                            break;
                        case Code.Blt_Un:
                        case Code.Blt_Un_S:
                            oper = isFloatingPoint ? "fcmp ult" : "icmp ult";
                            break;
                        case Code.Ble:
                        case Code.Ble_S:
                            oper = isFloatingPoint ? "fcmp ole" : "icmp sle";
                            break;
                        case Code.Ble_Un:
                        case Code.Ble_Un_S:
                            oper = isFloatingPoint ? "fcmp ule" : "icmp ule";
                            break;
                        case Code.Bgt:
                        case Code.Bgt_S:
                            oper = isFloatingPoint ? "fcmp ogt" : "icmp sgt";
                            break;
                        case Code.Bgt_Un:
                        case Code.Bgt_Un_S:
                            oper = isFloatingPoint ? "fcmp ugt" : "icmp ugt";
                            break;
                        case Code.Bge:
                        case Code.Bge_S:
                            oper = isFloatingPoint ? "fcmp oge" : "icmp sge";
                            break;
                        case Code.Bge_Un:
                        case Code.Bge_Un_S:
                            oper = isFloatingPoint ? "fcmp uge" : "icmp uge";
                            break;
                    }

                    this.BinaryOper(writer, opCode, oper, GetOperandOptions(isFloatingPoint) | OperandOptions.CastPointersToBytePointer | OperandOptions.AdjustIntTypes);
                    if (!opCode.UseAsConditionalExpression)
                    {
                        writer.WriteLine(string.Empty);
                        this.WriteCondBranch(writer, opCode);
                    }

                    break;
                case Code.Brtrue:
                case Code.Brtrue_S:
                case Code.Brfalse:
                case Code.Brfalse_S:

                    var forTrue = opCode.Any(Code.Brtrue, Code.Brtrue_S) ? "ne" : "eq";
                    var resultOf = this.ResultOf(opCode.OpCodeOperands[0]);

                    opts = OperandOptions.GenerateResult | OperandOptions.CastPointersToBytePointer;
                    this.UnaryOper(writer, opCode, "icmp " + forTrue, options: opts);

                    if (resultOf.IType.IsValueType() && !resultOf.IType.UseAsClass)
                    {
                        writer.WriteLine(", 0");
                    }
                    else
                    {
                        writer.WriteLine(", null");
                    }

                    if (!opCode.UseAsConditionalExpression)
                    {
                        this.WriteCondBranch(writer, opCode);
                    }

                    break;
                case Code.Br:
                case Code.Br_S:

                    writer.Write(string.Concat("br label %.a", opCode.JumpAddress()));

                    break;
                case Code.Leave:
                case Code.Leave_S:

                    writer.WriteLine("; Leave ");
                    if (this.tryScopes.Count > 0)
                    {
                        var tryClause = this.tryScopes.Peek();
                        var finallyClause = tryClause.Catches.FirstOrDefault(c => c.Flags.HasFlag(ExceptionHandlingClauseOptions.Finally));
                        if (finallyClause != null)
                        {
                            finallyClause.FinallyJumps.Add(string.Concat(".a", opCode.JumpAddress()));
                            this.WriteFinallyLeave(finallyClause);
                        }
                        else
                        {
                            writer.Write(string.Concat("br label %.a", opCode.JumpAddress()));
                        }
                    }
                    else
                    {
                        writer.Write(string.Concat("br label %.a", opCode.JumpAddress()));
                    }

                    break;
                case Code.Ceq:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fcmp oeq" : "icmp eq", GetOperandOptions(isFloatingPoint) | OperandOptions.CastPointersToBytePointer | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Clt:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fcmp olt" : "icmp slt", GetOperandOptions(isFloatingPoint) | OperandOptions.CastPointersToBytePointer | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Clt_Un:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fcmp ult" : "icmp ult", GetOperandOptions(isFloatingPoint) | OperandOptions.CastPointersToBytePointer | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Cgt:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fcmp ogt" : "icmp sgt", GetOperandOptions(isFloatingPoint) | OperandOptions.CastPointersToBytePointer | OperandOptions.AdjustIntTypes);
                    break;
                case Code.Cgt_Un:
                    isFloatingPoint = this.IsFloatingPointOp(opCode);
                    this.BinaryOper(writer, opCode, isFloatingPoint ? "fcmp ugt" : "icmp ugt", GetOperandOptions(isFloatingPoint) | OperandOptions.CastPointersToBytePointer | OperandOptions.AdjustIntTypes);
                    break;

                case Code.Conv_R4:
                case Code.Conv_R_Un:
                    this.LlvmConvert(opCode, "fptrunc", "sitofp", "float", false, this.ResolveType("System.Single"));
                    break;

                case Code.Conv_R8:
                    this.LlvmConvert(opCode, "fpext", "sitofp", "double", false, this.ResolveType("System.Double"));
                    break;

                case Code.Conv_I1:
                case Code.Conv_Ovf_I1:
                case Code.Conv_Ovf_I1_Un:
                    this.LlvmConvert(opCode, "fptosi", "trunc", "i8", false, this.ResolveType("System.SByte"), this.ResolveType("System.Byte"));
                    break;

                case Code.Conv_U1:
                case Code.Conv_Ovf_U1:
                case Code.Conv_Ovf_U1_Un:
                    this.LlvmConvert(opCode, "fptoui", "trunc", "i8", false, this.ResolveType("System.SByte"), this.ResolveType("System.Byte"));
                    break;

                case Code.Conv_I2:
                case Code.Conv_Ovf_I2:
                case Code.Conv_Ovf_I2_Un:
                    this.LlvmConvert(opCode, "fptosi", "trunc", "i16", false, this.ResolveType("System.Int16"), this.ResolveType("System.UInt16"), this.ResolveType("System.Char"));
                    break;

                case Code.Conv_U2:
                case Code.Conv_Ovf_U2:
                case Code.Conv_Ovf_U2_Un:
                    this.LlvmConvert(opCode, "fptoui", "trunc", "i16", false, this.ResolveType("System.Int16"), this.ResolveType("System.UInt16"), this.ResolveType("System.Char"));
                    break;

                case Code.Conv_I:
                case Code.Conv_Ovf_I:
                case Code.Conv_Ovf_I_Un:
                    this.LlvmConvert(opCode, "fptoui", "trunc", "i32", true, this.ResolveType("System.Int32"), this.ResolveType("System.UInt32"));
                    break;

                case Code.Conv_I4:
                case Code.Conv_Ovf_I4:
                case Code.Conv_Ovf_I4_Un:
                    this.LlvmConvert(opCode, "fptoui", "trunc", "i32", false, this.ResolveType("System.Int32"), this.ResolveType("System.UInt32"));
                    break;

                case Code.Conv_U:
                case Code.Conv_Ovf_U:
                case Code.Conv_Ovf_U_Un:
                    this.LlvmConvert(opCode, "fptosi", "trunc", "i32", true, this.ResolveType("System.Int32"), this.ResolveType("System.UInt32"));
                    break;

                case Code.Conv_U4:
                case Code.Conv_Ovf_U4:
                case Code.Conv_Ovf_U4_Un:
                    this.LlvmConvert(opCode, "fptosi", "trunc", "i32", false, this.ResolveType("System.Int32"), this.ResolveType("System.UInt32"));
                    break;

                case Code.Conv_I8:
                case Code.Conv_Ovf_I8:
                case Code.Conv_Ovf_I8_Un:
                    this.LlvmConvert(opCode, "fptosi", "zext", "i64", false, this.ResolveType("System.Int64"), this.ResolveType("System.UInt64"));
                    break;

                case Code.Conv_U8:
                case Code.Conv_Ovf_U8:
                case Code.Conv_Ovf_U8_Un:
                    this.LlvmConvert(opCode, "fptoui", "zext", "i64", false, this.ResolveType("System.Int64"), this.ResolveType("System.UInt64"));
                    break;

                case Code.Castclass:

                    opCodeTypePart = opCode as OpCodeTypePart;
                    this.ActualWrite(writer, opCodeTypePart.OpCodeOperands[0]);
                    writer.WriteLine(string.Empty);

                    this.WriteCast(opCodeTypePart, opCodeTypePart.OpCodeOperands[0].Result, opCodeTypePart.Operand);

                    break;

                case Code.Isinst:

                    opCodeTypePart = opCode as OpCodeTypePart;
                    this.ActualWrite(writer, opCodeTypePart.OpCodeOperands[0]);
                    writer.WriteLine(string.Empty);

                    var fromType = opCodeTypePart.OpCodeOperands[0].Result;
                    var toType = opCodeTypePart.Operand;

                    this.WriteDynamicCast(writer, opCodeTypePart, fromType, toType);

                    break;

                case Code.Newobj:

                    var opCodeConstructorInfoPart = opCode as OpCodeConstructorInfoPart;
                    var declaringType = opCodeConstructorInfoPart.Operand.DeclaringType;

                    this.CheckIfExternalDeclarationIsRequired(declaringType);

                    this.WriteNew(opCodeConstructorInfoPart, declaringType);

                    if (!string.IsNullOrWhiteSpace(opCode.DestinationName))
                    {
                        opCode.Result.Type.UseAsClass = false;
                        this.WriteLlvmLoad(opCode, opCode.Result.ToFullyDefinedReference());
                    }

                    break;

                case Code.Newarr:

                    opCodeTypePart = opCode as OpCodeTypePart;
                    this.WriteNewArray(opCode, opCodeTypePart.Operand, opCode.OpCodeOperands[0]);

                    break;

                case Code.Initobj:

                    opCodeTypePart = opCode as OpCodeTypePart;

                    ////this.WriteNew(writer, opCode, opCodeTypePart.Operand);
                    writer.Write("; Initobj - TODO: finish");

                    break;

                case Code.Throw:

                    this.ActualWrite(writer, opCode.OpCodeOperands[0]);
                    writer.WriteLine(string.Empty);
                    this.WriteThrow(opCode, this.tryScopes.Count > 0 ? this.tryScopes.Peek().Catches.First() : null);

                    break;

                case Code.Rethrow:

                    this.WriteRethrow(
                        opCode, this.catchScopes.Count > 0 ? this.catchScopes.Peek() : null, this.tryScopes.Count > 0 ? this.tryScopes.Peek().Catches.First() : null);

                    break;

                case Code.Endfilter:
                case Code.Endfinally:
                    break;

                case Code.Pop:
                    break;

                case Code.Constrained:

                    // to solve the problem with referencing ValueType and Class type in Generic type
                    break;

                case Code.Switch:

                    var opCodeLabels = opCode as OpCodeLabelsPart;

                    this.UnaryOper(writer, opCode, "switch");

                    var switchValueType = opCodeLabels.OpCodeOperands[0].Result.Type;

                    index = 0;
                    writer.Write(", label %.a{0} [ ", opCode.GroupAddressEnd);

                    foreach (var label in opCodeLabels.Operand)
                    {
                        switchValueType.WriteTypePrefix(writer);
                        writer.Write(" {0}, label %.a{1} ", index, opCodeLabels.JumpAddress(index++));
                    }

                    writer.WriteLine("]");

                    writer.Indent--;
                    writer.WriteLine(string.Concat(".a", opCode.GroupAddressEnd, ':'));
                    writer.Indent++;

                    opCode.NextOpCode(this).JumpProcessed = true;

                    break;

                case Code.Nop:

                    // to support settings exceptions
                    if (opCode.ReadExceptionFromStack)
                    {
                        opCode.Result = new LlvmResult(this.resultNumberIncremental, null);
                    }

                    break;
            }
        }

        private void WriteGetFieldAccessAndSaveToField(OpCodeFieldInfoPart opCodeFieldInfoPart)
        {
            var writer = this.Output;

            var directResult1 = this.PreProcessOperand(writer, opCodeFieldInfoPart, 1);
            this.WriteFieldAccess(writer, opCodeFieldInfoPart);
            writer.WriteLine(string.Empty);

            var fieldType = opCodeFieldInfoPart.Operand.FieldType;

            WriteSaveToField(opCodeFieldInfoPart, fieldType);
        }

        public void WriteSaveToField(OpCodePart opCodePart, IType fieldType, int valueOperand = 1)
        {
            var writer = this.Output;

            if (opCodePart.OpCodeOperands == null)
            {
                return;
            }

            bool directResult1 = IsDirectValue(opCodePart.OpCodeOperands[valueOperand]);

            if (fieldType.IsStructureType())
            {
                opCodePart.DestinationName = opCodePart.Result.ToString();
                if (directResult1)
                {
                    this.WriteLlvmLoad(opCodePart, fieldType, new FullyDefinedReference(GetDirectName(opCodePart.OpCodeOperands[valueOperand]), fieldType));
                }
                else
                {
                    this.WriteLlvmLoad(opCodePart, fieldType, opCodePart.OpCodeOperands[valueOperand].Result);
                }
            }
            else
            {
                var opts = valueOperand == 1 ? OperandOptions.TypeIsInSecondOperand | OperandOptions.AdjustIntTypes : OperandOptions.AdjustIntTypes;

                this.ProcessOperator(writer, opCodePart, "store", fieldType, options: opts);
                this.PostProcessOperand(writer, opCodePart, valueOperand, directResult1);
                writer.Write(", ");
                opCodePart.Result.Type.WriteTypePrefix(writer);
                writer.Write("* ");
                WriteResultNumber(opCodePart.Result);
            }
        }

        private void LoadElement(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.Result != null)
            {
                return;
            }

            var actualLoad = true;
            IType type = null;
            switch (opCode.ToCode())
            {
                case Code.Ldelem:
                case Code.Ldelem_I:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Ldelem_I1:
                    type = this.ResolveType("System.SByte");
                    break;
                case Code.Ldelem_I2:
                    type = this.ResolveType("System.Int16");
                    break;
                case Code.Ldelem_I4:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Ldelem_U1:
                    type = this.ResolveType("System.Byte");
                    break;
                case Code.Ldelem_U2:
                    type = this.ResolveType("System.UInt16");
                    break;
                case Code.Ldelem_U4:
                    type = this.ResolveType("System.UInt32");
                    break;
                case Code.Ldelem_I8:
                    type = this.ResolveType("System.Int64");
                    break;
                case Code.Ldelem_R4:
                    type = this.ResolveType("System.Single");
                    break;
                case Code.Ldelem_R8:
                    type = this.ResolveType("System.Double");
                    break;
                case Code.Ldelem_Ref:
                    type = this.GetTypeOfReference(opCode);
                    break;
                case Code.Ldelema:
                    actualLoad = false;
                    break;
            }

            this.BinaryOper(
                writer,
                opCode,
                "getelementptr inbounds",
                options: OperandOptions.GenerateResult | OperandOptions.DetectAndWriteTypeInSecondOperand,
                resultType: type);

            if (actualLoad)
            {
                writer.WriteLine(string.Empty);

                var accessIndexResultNumber = opCode.Result;
                opCode.Result = null;
                this.WriteLlvmLoad(opCode, type, accessIndexResultNumber);
            }
        }

        private void SaveElement(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            bool directResult1;
            IType type = null;

            switch (opCode.ToCode())
            {
                case Code.Stelem:
                case Code.Stelem_I:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Stelem_I1:
                    type = this.ResolveType("System.SByte");
                    break;
                case Code.Stelem_I2:
                    type = this.ResolveType("System.Int16");
                    break;
                case Code.Stelem_I4:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Stelem_I8:
                    type = this.ResolveType("System.Int64");
                    break;
                case Code.Stelem_R4:
                    type = this.ResolveType("System.Single");
                    break;
                case Code.Stelem_R8:
                    type = this.ResolveType("System.Double");
                    break;
                case Code.Stelem_Ref:
                    type = this.GetTypeOfReference(opCode);
                    break;
            }

            this.BinaryOper(
                writer,
                opCode,
                "getelementptr inbounds",
                options: OperandOptions.GenerateResult | OperandOptions.DetectAndWriteTypeInSecondOperand,
                resultType: type);
            writer.WriteLine(string.Empty);

            var operandIndex = 2;

            directResult1 = this.PreProcessOperand(writer, opCode, operandIndex);

            this.AdjustIntConvertableTypes(writer, opCode.OpCodeOperands[operandIndex], directResult1, type);

            this.ProcessOperator(writer, opCode, "store", type, operand1: 2, operand2: -1);
            this.PostProcessOperand(writer, opCode, operandIndex, directResult1);

            writer.Write(", ");
            type.WriteTypePrefix(writer, type.IsStructureType());
            writer.Write("* {0}", opCode.Result);
        }

        private void SaveIndirect(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            bool directResult1;
            IType type = null;

            switch (opCode.ToCode())
            {
                case Code.Stind_I:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Stind_I1:
                    type = this.ResolveType("System.Byte");
                    break;
                case Code.Stind_I2:
                    type = this.ResolveType("System.Int16");
                    break;
                case Code.Stind_I4:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Stind_I8:
                    type = this.ResolveType("System.Int64");
                    break;
                case Code.Stind_R4:
                    type = this.ResolveType("System.Single");
                    break;
                case Code.Stind_R8:
                    type = this.ResolveType("System.Double");
                    break;
                case Code.Stind_Ref:
                    type = this.GetTypeOfReference(opCode);
                    break;
            }

            var destinationType = opCode.OpCodeOperands[0].Result.Type;
            if (destinationType.IsPointer && destinationType.GetElementType().TypeNotEquals(type))
            {
                // adjust destination type, cast pointer to pointer of type
                this.WriteBitcast(opCode, opCode.OpCodeOperands[0].Result, type);
                opCode.OpCodeOperands[0].Result = opCode.Result;
                destinationType = type.ToPointerType();
                writer.WriteLine(string.Empty);
            }

            if (!destinationType.IsPointer
                && destinationType.IntTypeBitSize() >= (LlvmWriter.PointerSize * 8)
                && destinationType.IntTypeBitSize() != type.IntTypeBitSize()
                && !opCode.OpCodeOperands[0].Result.Type.IsPointer
                && !opCode.OpCodeOperands[0].Result.Type.IsByRef)
            {
                // adjust destination type, cast pointer to pointer of type
                this.WriteIntToPtr(opCode, opCode.OpCodeOperands[0].Result.ToFullyDefinedReference(), type);
                opCode.OpCodeOperands[0].Result = opCode.Result;
                destinationType = type.ToPointerType();
                writer.WriteLine(string.Empty);
            }

            directResult1 = this.PreProcessOperand(writer, opCode, 0);

            this.UnaryOper(writer, opCode, 1, "store", type, options: OperandOptions.AdjustIntTypes);
            writer.Write(", ");

            destinationType.WriteTypePrefix(writer);
            this.PostProcessOperand(writer, opCode, 0, directResult1);
        }

        public void LoadIndirect(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.Result != null)
            {
                return;
            }

            bool directResult1;
            IType type = null;

            switch (opCode.ToCode())
            {
                case Code.Ldind_I:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Ldind_I1:
                    type = this.ResolveType("System.SByte");
                    break;
                case Code.Ldind_U1:
                    type = this.ResolveType("System.Byte");
                    break;
                case Code.Ldind_I2:
                    type = this.ResolveType("System.Int16");
                    break;
                case Code.Ldind_U2:
                    type = this.ResolveType("System.UInt16");
                    break;
                case Code.Ldind_I4:
                    type = this.ResolveType("System.Int32");
                    break;
                case Code.Ldind_U4:
                    type = this.ResolveType("System.UInt32");
                    break;
                case Code.Ldind_I8:
                    type = this.ResolveType("System.Int64");
                    break;
                case Code.Ldind_R4:
                    type = this.ResolveType("System.Single");
                    break;
                case Code.Ldind_R8:
                    type = this.ResolveType("System.Double");
                    break;
                case Code.Ldind_Ref:
                    type = this.GetTypeOfReference(opCode);
                    break;
            }

            this.LoadIndirect(writer, opCode, type);
        }

        public void LoadIndirect(LlvmIndentedTextWriter writer, OpCodePart opCode, IType type)
        {
            bool directResult1;
            LlvmResult accessIndexResultNumber2;
            directResult1 = false;

            // next code fixing issue with using Code.Ldind to load first value in value types
            var resultOfOperand0 = opCode.OpCodeOperands[0].Result;
            var isUsedAsClass = resultOfOperand0 != null && resultOfOperand0.Type.UseAsClass;
            if (isUsedAsClass)
            {
                resultOfOperand0.Type.UseAsClass = false;
            }

            var isValueType = resultOfOperand0 != null && resultOfOperand0.Type.IsValueType;
            if (isValueType && isUsedAsClass)
            {
                resultOfOperand0.Type.UseAsClass = true;

                // write first field access
                this.WriteFieldAccess(writer, opCode, 1);
                writer.WriteLine(string.Empty);
                accessIndexResultNumber2 = opCode.Result;
                type = opCode.Result.Type;
            }
            else
            {
                if (isUsedAsClass)
                {
                    resultOfOperand0.Type.UseAsClass = true;
                }

                directResult1 = this.PreProcessOperand(writer, opCode, 0);
                accessIndexResultNumber2 = opCode.OpCodeOperands[0].Result;
            }

            opCode.Result = null;

            if (directResult1)
            {
                this.WriteLlvmLoad(opCode, type, new FullyDefinedReference(GetDirectName(opCode.OpCodeOperands[0]), type));
            }
            else
            {
                this.WriteLlvmLoad(opCode, type, accessIndexResultNumber2);
            }

            if (!isUsedAsClass && resultOfOperand0 != null)
            {
                resultOfOperand0.Type.UseAsClass = false;
            }
        }

        public void WriteDynamicCast(
            LlvmIndentedTextWriter writer, OpCodePart opCodeTypePart, LlvmResult fromType, IType toType)
        {
            this.WriteCast(opCodeTypePart, fromType, this.ResolveType("System.Byte"));
            writer.WriteLine(string.Empty);

            var firstCastToBytesResult = opCodeTypePart.Result;

            var dynamicCastResultNumber = this.WriteSetResultNumber(opCodeTypePart, this.ResolveType("System.Byte").ToPointerType());

            writer.Write("call i8* @__dynamic_cast(i8* {0}, i8* bitcast (", firstCastToBytesResult);
            fromType.Type.WriteRttiClassInfoDeclaration(writer);
            writer.Write("* @\"{0}\" to i8*), i8* bitcast (", fromType.Type.GetRttiInfoName());
            toType.WriteRttiClassInfoDeclaration(writer);
            writer.WriteLine("* @\"{0}\" to i8*), i32 0)", toType.GetRttiInfoName());
            writer.WriteLine(string.Empty);

            toType.UseAsClass = true;
            this.WriteBitcast(opCodeTypePart, dynamicCastResultNumber, toType);

            this.typeRttiDeclRequired.Add(fromType.Type);
            this.typeRttiDeclRequired.Add(toType);
        }

        private static OpCodePart GetTypedIntZeroCode(IType intType)
        {
            switch (intType.IntTypeBitSize())
            {
                case 64:
                    return new OpCodeInt64Part(OpCodesEmit.Ldc_I8, 0, 0, 0);
                default:
                    return new OpCodePart(OpCodesEmit.Ldc_I4_0, 0, 0);
            }
        }

        public void WriteConvertValueTypeToReferenceType(OpCodePart opCode, IType declaringType)
        {
            var writer = this.Output;

            writer.WriteLine(string.Empty);
            this.CheckIfExternalDeclarationIsRequired(declaringType);
            this.WriteNewWithoutCallingConstructor(opCode, declaringType);

            var newObjectResult = opCode.Result;

            writer.WriteLine(string.Empty);
            writer.WriteLine("; Copy data");

            // write access to a field
            var directResult1 = this.PreProcessOperand(writer, opCode, 0);

            if (!declaringType.IsStructureType() && declaringType.FullName != "System.DateTime" && declaringType.FullName != "System.Decimal")
            {
                this.WriteFieldAccess(writer, opCode, declaringType.ToClass(), 1, opCode.Result.ToFullyDefinedReference());
                writer.WriteLine(string.Empty);
            }

            var fieldType = declaringType;

            fieldType.UseAsClass = false;

            WriteSaveToField(opCode, fieldType, 0);

            writer.WriteLine(string.Empty);
            writer.WriteLine("; End of Copy data");

            opCode.Result = newObjectResult;
            opCode.Result.Type.UseAsClass = true;
        }

        // returns true if destination type should be changed
        public bool AdjustIntConvertableTypes(LlvmIndentedTextWriter writer, OpCodePart opCode, bool isDirectResult, IType destType)
        {
            if (isDirectResult)
            {
                return false;
            }

            if (!destType.IsPointer && !opCode.Result.Type.IsPointer && destType.IsIntValueTypeCastRequired(opCode.Result.Type))
            {
                this.LlvmIntConvert(opCode, "zext", "i" + destType.IntTypeBitSize());
                writer.WriteLine(string.Empty);
                return false;
            }

            // pointer to int, int to pointerf
            if (destType.IntTypeBitSize() > 0 && !destType.IsPointer && opCode.Result.Type.IsPointer)
            {
                this.LlvmIntConvert(opCode, "ptrtoint", "i" + destType.IntTypeBitSize());
                writer.WriteLine(string.Empty);
                return true;
            }

            if (opCode.Result.Type.IntTypeBitSize() > 0 && destType.IsPointer && !opCode.Result.Type.IsPointer)
            {
                this.LlvmIntConvert(opCode, "inttoptr", "i" + destType.GetElementType().IntTypeBitSize() + "*");
                writer.WriteLine(string.Empty);
                return true;
            }

            return false;
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="op">
        /// </param>
        /// <param name="options">
        /// </param>
        private void BinaryOper(LlvmIndentedTextWriter writer, OpCodePart opCode, string op, OperandOptions options = OperandOptions.None, IType resultType = null)
        {
            if (opCode.HasResult)
            {
                return;
            }

            var directResult1 = this.PreProcessOperand(writer, opCode, 0, options);
            var directResult2 = this.PreProcessOperand(writer, opCode, 1, options);

            this.ProcessOperator(writer, opCode, op, options: options, resultType: resultType);

            this.PostProcessOperand(writer, opCode, 0, directResult1);
            writer.Write(',');
            this.PostProcessOperand(writer, opCode, 1, directResult2, options.HasFlag(OperandOptions.DetectAndWriteTypeInSecondOperand));
        }

        /// <summary>
        /// </summary>
        /// <param name="fieldInfo">
        /// </param>
        /// <param name="type">
        /// </param>
        /// <returns>
        /// </returns>
        /// <exception cref="KeyNotFoundException">
        /// </exception>
        private int CalculateFieldIndex(IField fieldInfo, IType type)
        {
            var list = IlReader.Fields(type).Where(t => !t.IsStatic).ToList();
            var index = 0;

            while (index < list.Count && list[index].NameNotEquals(fieldInfo))
            {
                index++;
            }

            if (index == list.Count)
            {
                throw new KeyNotFoundException();
            }

            index += CalculateFirstFieldIndex(type);

            this.indexByFieldInfo[GetFullFieldName(fieldInfo)] = index;

            return index;
        }

        private int CalculateFirstFieldIndex(IType type)
        {
            var index = 0;
            // add shift for virtual table
            if (type.IsRootOfVirtualTable())
            {
                index++;
            }

            // add shift for base type
            if (type.BaseType != null)
            {
                index++;
            }

            // add shift for interfaces
            if (type.BaseType == null)
            {
                index += type.GetInterfaces().Count();
            }
            else
            {
                var baseInterfaces = type.BaseType.GetInterfaces();
                index += type.GetInterfaces().Count(i => !baseInterfaces.Contains(i));
            }

            return index;
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        /// <param name="requiredType">
        /// </param>
        /// <param name="options">
        /// </param>
        /// <param name="castFrom">
        /// </param>
        /// <returns>
        /// </returns>
        private IType DetectTypePrefix(OpCodePart opCode, IType requiredType, OperandOptions options, out IType castFrom, out IType intAdjustment, out bool intAdjustSecondOperand, int operand1 = 0, int operand2 = 1)
        {
            castFrom = null;
            intAdjustment = null;
            intAdjustSecondOperand = false;

            var res1 = options.HasFlag(OperandOptions.TypeIsInOperator)
                           ? this.ResultOf(opCode)
                           : opCode.OpCodeOperands != null && operand1 >=0 && opCode.OpCodeOperands.Length > operand1 ? this.ResultOf(opCode.OpCodeOperands[operand1]) : null;

            var res2 = opCode.OpCodeOperands != null && operand2 >= 0 && opCode.OpCodeOperands.Length > operand2 ? this.ResultOf(opCode.OpCodeOperands[operand2]) : null;

            // write type
            var effectiveType = this.ResolveType("System.Void");

            if (options.HasFlag(OperandOptions.CastPointersToBytePointer) && res1 != null && res2 != null && res1.IsPointerAccessRequired && res2.IsPointerAccessRequired)
            {
                castFrom = res1.IType;
                effectiveType = res2.IType;
            }
            else if (requiredType != null)
            {
                if (options.HasFlag(OperandOptions.AdjustIntTypes) && res1 != null && res1.IType != null && res2 != null && res2.IType != null
                    && res1.IType.TypeEquals(res2.IType) && res1.IType.TypeNotEquals(requiredType) && res1.IType.TypeEquals(this.ResolveType("System.Boolean"))
                    && requiredType.TypeEquals(this.ResolveType("System.Byte")))
                {
                    effectiveType = res1.IType;
                }
                else
                {
                    effectiveType = requiredType;
                }
            }
            else if (options.HasFlag(OperandOptions.TypeIsInOperator) || opCode.OpCodeOperands != null && opCode.OpCodeOperands.Length > operand1)
            {
                if ((!options.HasFlag(OperandOptions.TypeIsInSecondOperand) || (res2.IsConst ?? false)) && !(res1.IsConst ?? false) || res2 == null)
                {
                    effectiveType = res1.IType;
                }
                else
                {
                    effectiveType = res2.IType;
                }
            }

            if (res1 != null
                && res1.IType.TypeNotEquals(effectiveType)
                && (res1.IType.IsClass || res1.IType.IsArray || res1.IType.IsInterface || res1.IType.IsDelegate)
                && effectiveType.IsAssignableFrom(res1.IType))
            {
                castFrom = res1.IType;
            }

            if (options.HasFlag(OperandOptions.AdjustIntTypes))
            {
                var firstType = res1 != null && res1.IType != null && !(res1.IsConst ?? false) && !options.HasFlag(OperandOptions.TypeIsInSecondOperand)
                                    ? res1.IType
                                    : res2 != null && res2.IType != null && !(res2.IsConst ?? false) ? res2.IType : null;

                IType secondType = null;
                if (firstType != null)
                {
                    if (res2 != null && res2.IType != null && !(res2.IsConst ?? false))
                    {
                        secondType = res2.IType;
                    }

                    if (requiredType != null)
                    {
                        secondType = requiredType;
                    }

                    if (secondType != null)
                    {
                        if (firstType.IsIntValueTypeCastRequired(secondType))
                        {
                            intAdjustSecondOperand = true;
                            intAdjustment = firstType;
                        }

                        if (secondType.IsIntValueTypeCastRequired(firstType))
                        {
                            intAdjustSecondOperand = false;
                            intAdjustment = secondType;
                        }

                        // pointer to int, int to pointer
                        if (!firstType.IsByRef && !firstType.IsPointer && firstType.IntTypeBitSize() > 0 && secondType.IsPointer)
                        {
                            intAdjustSecondOperand = true;
                            intAdjustment = firstType;

                            if (requiredType != null && requiredType.IsPointer)
                            {
                                intAdjustment = secondType;
                            }
                        }

                        if (!secondType.IsByRef && !secondType.IsPointer && secondType.IntTypeBitSize() > 0 && firstType.IsPointer)
                        {
                            intAdjustSecondOperand = false;
                            intAdjustment = secondType;

                            if (requiredType != null && requiredType.IsPointer)
                            {
                                intAdjustment = firstType;
                            }
                        }
                    }
                }
            }

            return effectiveType;
        }

        private IType DetectTypePrefix(OpCodePart operand1, OpCodePart operand2, out IType castFrom)
        {
            castFrom = null;

            var res1 = this.ResultOf(operand1);
            var res2 = this.ResultOf(operand2);

            // write type
            var effectiveType = this.ResolveType("System.Void");

            if ((res2.IsConst ?? false) && !(res1.IsConst ?? false) || res2 == null)
            {
                effectiveType = res1.IType;
            }
            else
            {
                effectiveType = res2.IType;
            }

            if (res1 != null && res1.IType != effectiveType && (res1.IType.IsClass || res1.IType.IsArray) && effectiveType.IsAssignableFrom(res1.IType))
            {
                castFrom = res1.IType;
            }

            return effectiveType;
        }

        /// <summary>
        /// </summary>
        /// <param name="index">
        /// </param>
        /// <returns>
        /// </returns>
        private string GetArgVarName(int index)
        {
            return string.Concat("%.", this.Parameters[index].Name);
        }

        /// <summary>
        /// </summary>
        /// <param name="methodBase">
        /// </param>
        /// <param name="ownerOfExplicitInterface">
        /// </param>
        /// <returns>
        /// </returns>
        private string GetFullMethodName(IMethod methodBase, IType ownerOfExplicitInterface = null)
        {
            if (methodBase.IsInternalCall)
            {
                return string.Concat('@', methodBase.Name.StartsWith("llvm_") ? methodBase.Name.Replace('_', '.') : methodBase.Name);
            }

            var sb = new StringBuilder();
            sb.Append("@\"");

            if (ownerOfExplicitInterface != null)
            {
                sb.Append(methodBase.ToString(ownerOfExplicitInterface));
            }
            else
            {
                sb.Append(methodBase.ToString());
            }

            sb.Append('"');

            return sb.ToString();
        }

        /// <summary>
        /// </summary>
        /// <param name="str">
        /// </param>
        /// <returns>
        /// </returns>
        private int GetStringIndex(string str)
        {
            var idx = ++this.stringIndexIncremental;
            this.stringStorage[idx] = str;
            return idx;
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        /// <returns>
        /// </returns>
        private IType GetTypeOfReference(OpCodePart opCode)
        {
            IType type = null;
            if (opCode.HasResult)
            {
                type = opCode.Result.Type;
            }
            else if (opCode.OpCodeOperands != null && opCode.OpCodeOperands.Length > 0 && opCode.OpCodeOperands[0].HasResult)
            {
                type = opCode.OpCodeOperands[0].Result.Type;
            }
            else
            {
                type = this.ResolveType("System.Byte").ToPointerType();
            }

            if (type.IsArray || type.IsByRef)
            {
                return type.GetElementType();
            }

            return type;
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        /// <returns>
        /// </returns>
        private bool IsFloatingPointOp(OpCodePart opCode)
        {
            return opCode.OpCodeOperands.Length > 0 && this.IsFloatingPointOpOperand(opCode.OpCodeOperands[0])
                   || opCode.OpCodeOperands.Length > 1 && this.IsFloatingPointOpOperand(opCode.OpCodeOperands[1]);
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        /// <returns>
        /// </returns>
        private bool IsFloatingPointOpOperand(OpCodePart opCode)
        {
            var op1ReturnResult = this.ResultOf(opCode);

            // TODO: result of unbox is null, fix it
            if (op1ReturnResult == null || op1ReturnResult.IType == null)
            {
                return false;
            }

            var op1IsReal = op1ReturnResult.IType.IsReal();
            return op1IsReal;
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="operand">
        /// </param>
        /// <param name="directResult">
        /// </param>
        /// <param name="detectAndWriteTypePrefix">
        /// </param>
        public void PostProcess(LlvmIndentedTextWriter writer, OpCodePart operand, bool directResult, bool detectAndWriteTypePrefix = false, IType forcedType = null)
        {
            writer.Write(' ');

            if (directResult)
            {
                if (forcedType != null)
                {
                    forcedType.WriteTypePrefix(writer);
                    writer.Write(' ');
                }
                else if (detectAndWriteTypePrefix)
                {
                    IType castFrom;
                    IType intAdjustment;
                    bool intAdjustSecondOperand;
                    var effectiveType = this.DetectTypePrefix(operand, null, OperandOptions.TypeIsInOperator, out castFrom, out intAdjustment, out intAdjustSecondOperand);
                    (effectiveType ?? this.ResolveType("System.Void")).WriteTypePrefix(writer);
                    writer.Write(' ');
                }

                this.ActualWrite(writer, operand);
            }
            else
            {
                if (forcedType != null)
                {
                    forcedType.WriteTypePrefix(writer);
                    writer.Write(' ');
                }
                else if (detectAndWriteTypePrefix)
                {
                    (operand.Result.Type ?? this.ResolveType("System.Void")).WriteTypePrefix(writer);
                    writer.Write(' ');
                }

                WriteResultNumber(operand.Result);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="operandOpCode">
        /// </param>
        /// <param name="options">
        /// </param>
        /// <returns>
        /// </returns>
        public bool PreProcess(LlvmIndentedTextWriter writer, OpCodePart operandOpCode, OperandOptions options = OperandOptions.None)
        {
            if (!this.IsDirectValue(operandOpCode))
            {
                if (!operandOpCode.HasResult)
                {
                    this.ActualWrite(writer, operandOpCode);
                    writer.WriteLine(string.Empty);
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="operandIndex">
        /// </param>
        /// <param name="op">
        /// </param>
        /// <param name="requiredType">
        /// </param>
        /// <param name="resultType">
        /// </param>
        /// <param name="options">
        /// </param>
        public void UnaryOper(
            LlvmIndentedTextWriter writer,
            OpCodePart opCode,
            int operandIndex,
            string op,
            IType requiredType = null,
            IType resultType = null,
            OperandOptions options = OperandOptions.None)
        {
            var directResult1 = this.PreProcessOperand(writer, opCode, operandIndex, options);

            this.ProcessOperator(writer, opCode, op, requiredType, resultType, options, operand1: operandIndex);

            if (!options.HasFlag(OperandOptions.IgnoreOperand))
            {
                this.PostProcessOperand(writer, opCode, operandIndex, directResult1);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="name">
        /// </param>
        /// <param name="type">
        /// </param>
        /// <param name="isThis">
        /// </param>
        private void WriteArgumentCopyDeclaration(string name, IType type, bool isThis = false)
        {
            if (!isThis && type.IsStructureType())
            {
                return;
            }

            if (isThis)
            {
                type.UseAsClass = true;
            }

            this.Output.Write("%.{0} = ", name);

            // for value types
            this.Output.Write("alloca ");
            type.WriteTypePrefix(this.Output, type.IsStructureType() || isThis);
            this.Output.Write(", align " + PointerSize);
            this.Output.WriteLine(string.Empty);

            this.Output.Write("store ");
            type.WriteTypePrefix(this.Output, type.IsStructureType() || isThis);
            this.Output.Write(" %{0}", name);
            this.Output.Write(", ");
            type.WriteTypePrefix(this.Output, type.IsStructureType() || isThis);

            this.Output.Write("* %.{0}", name);
            this.Output.Write(", align " + PointerSize);
            this.Output.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        /// <param name="parametersInfo">
        /// </param>
        /// <param name="hasThis">
        /// </param>
        private void WriteArgumentCopyDeclarations(IEnumerable<IParameter> parametersInfo, bool hasThis)
        {
            if (hasThis)
            {
                this.WriteArgumentCopyDeclaration("this", this.ThisType, true);
            }

            foreach (var parameterInfo in parametersInfo)
            {
                this.WriteArgumentCopyDeclaration(parameterInfo.Name, parameterInfo.ParameterType);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void WriteCaseAndLabels(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.JumpDestination != null && opCode.JumpDestination.Count > 0)
            {
                var previousOpCode = opCode.PreviousOpCode(this);
                var splitBlock = previousOpCode == null
                                 || (previousOpCode != null
                                     && (previousOpCode.OpCode.FlowControl == FlowControl.Next || previousOpCode.OpCode.FlowControl == FlowControl.Call));
                if (splitBlock)
                {
                    // we need to fix issue with blocks in llvm http://zanopia.wordpress.com/2010/09/14/understanding-llvm-assembly-with-fractals-part-i/
                    writer.WriteLine(string.Concat("br label %.a", opCode.AddressStart));
                }

                if (splitBlock || !opCode.JumpProcessed)
                {
                    writer.Indent--;
                    writer.WriteLine(string.Concat(".a", opCode.AddressStart, ':'));
                    writer.Indent++;
                }
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void WriteCatchFinnallyEnd(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.CatchOrFinallyEnd == null)
            {
                return;
            }

            var eh = opCode.CatchOrFinallyEnd;
            opCode.CatchOrFinallyEnd = null;
            writer.WriteLine(string.Empty);
            this.WriteCatchEnd(opCode, eh, this.tryScopes.Count > 0 ? this.tryScopes.Peek().Catches.First() : null);
        }

        /// <summary>
        /// </summary>
        /// <param name="opCode">
        /// </param>
        private void WriteCatchFinnallyCleanUpEnd(OpCodePart opCode)
        {
            if (opCode.CatchOrFinallyEnd == null)
            {
                return;
            }

            var eh = opCode.CatchOrFinallyEnd;
            opCode.CatchOrFinallyEnd = null;
            var ehPopped = this.catchScopes.Pop();
            Debug.Assert(ehPopped == eh, "Mismatch of exception handlers");
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void WriteCondBranch(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            writer.WriteLine("br i1 {0}, label %.a{1}, label %.a{2}", opCode.Result, opCode.JumpAddress(), opCode.GroupAddressEnd);
            writer.Indent--;
            writer.WriteLine(string.Concat(".a", opCode.GroupAddressEnd, ':'));
            writer.Indent++;

            opCode.NextOpCode(this).JumpProcessed = true;
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void WriteExceptionHandlers(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.ExceptionHandlers == null)
            {
                return;
            }

            writer.WriteLine(string.Empty);
            this.WriteCatchProlog(opCode);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCodeFieldInfoPart">
        /// </param>
        public void WriteFieldAccess(LlvmIndentedTextWriter writer, OpCodeFieldInfoPart opCodeFieldInfoPart)
        {
            writer.WriteLine("; Access to '{0}' field", opCodeFieldInfoPart.Operand.Name);

            var operand = this.ResultOf(opCodeFieldInfoPart.OpCodeOperands[0]);
            var opts = OperandOptions.GenerateResult;

            operand.IType.UseAsClass = true;

            this.UnaryOper(writer, opCodeFieldInfoPart, "getelementptr inbounds", operand.IType, opCodeFieldInfoPart.Operand.FieldType, options: opts);
            this.WriteFieldIndex(writer, operand.IType, opCodeFieldInfoPart.Operand);
        }

        /// <summary>
        /// fixing issue with Code.Ldind when you need to load first field value
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="OpCodePart">
        /// </param>
        public void WriteFieldAccess(LlvmIndentedTextWriter writer, OpCodePart opCodePart, int index)
        {
            writer.WriteLine("; Access to '#{0}' field", index);
            var operand = this.ResultOf(opCodePart.OpCodeOperands[0]);

            var classType = operand.IType;

            var opts = OperandOptions.GenerateResult;
            var field = IlReader.Fields(classType).Where(t => !t.IsStatic).Skip(index - 1).First();
            classType.UseAsClass = true;
            this.UnaryOper(writer, opCodePart, "getelementptr inbounds", classType, field.FieldType, options: opts);
            this.WriteFieldIndex(writer, classType, index);
        }

        public void WriteFieldAccess(LlvmIndentedTextWriter writer, OpCodePart opCodePart, IType classType, int index, FullyDefinedReference valueReference)
        {
            var field = IlReader.Fields(classType).Where(t => !t.IsStatic).Skip(index - 1).First();

            WriteSetResultNumber(opCodePart, field.FieldType);

            writer.Write("getelementptr inbounds ");
            valueReference.Type.ToClass().WriteTypePrefix(writer);
            writer.Write(" ");
            writer.Write(valueReference.Name);

            this.WriteFieldIndex(writer, classType, index);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="classType">
        /// </param>
        /// <param name="fieldInfo">
        /// </param>
        public void WriteFieldIndex(LlvmIndentedTextWriter writer, IType classType, IField fieldInfo)
        {
            var targetType = fieldInfo.DeclaringType;
            var type = classType;

            // first element for pointer (IType* + 0)
            writer.Write(", i32 0");

            while (type.TypeNotEquals(targetType))
            {
                type = type.BaseType;
                if (type == null)
                {
                    break;
                }

                // first index is base type index
                writer.Write(", i32 0");
            }

            // find index
            int index;
            if (!this.indexByFieldInfo.TryGetValue(GetFullFieldName(fieldInfo), out index))
            {
                index = this.CalculateFieldIndex(fieldInfo, type);
            }

            writer.Write(", i32 ");
            writer.Write(index);
        }

        private void WriteFieldIndex(LlvmIndentedTextWriter writer, IType classType, int index)
        {
            // first element for pointer (IType* + 0)
            writer.Write(", i32 0");
            writer.Write(", i32 ");
            writer.Write(index);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="type">
        /// </param>
        private void WriteGenericParameters(LlvmIndentedTextWriter writer, IType type)
        {
            var index = type.Name.IndexOf('`');
            var level = int.Parse(type.Name.Substring(index + 1));

            for (var i = 0; i < level; i++)
            {
                if (i > 0)
                {
                    writer.Write(", ");
                }

                writer.Write("typename ");

                IType generic = null;

                if (this.GenericMethodArguments != null)
                {
                    generic = this.GenericMethodArguments.Where(a => a.GenericParameterPosition == i).FirstOrDefault();
                }

                if (generic == null && this.TypeGenericArguments != null)
                {
                    generic = this.TypeGenericArguments.Where(a => a.GenericParameterPosition == i).FirstOrDefault();
                }

                if (generic != null)
                {
                    writer.Write(generic.Name);
                }
                else
                {
                    writer.Write("T");
                    writer.Write(i + 1);
                }
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        /// <param name="method">
        /// </param>
        private void WriteGetInterfaceOffsetToObjectRootPointer(LlvmIndentedTextWriter writer, OpCodePart opCode, IMethod method)
        {
            this.WriteSetResultNumber(opCode, this.ResolveType("System.Int32").ToPointerType());
            writer.Write("bitcast ");
            this.WriteMethodPointerType(writer, method);
            writer.Write("* ");
            WriteResultNumber(opCode.OpCodeOperands[0].Result);
            writer.Write(" to ");
            this.ResolveType("System.Int32").ToPointerType().WriteTypePrefix(writer);
            writer.WriteLine(string.Empty);

            var res = opCode.Result;
            var offsetResult = this.WriteSetResultNumber(opCode, this.ResolveType("System.Int32"));
            writer.Write("getelementptr ");
            this.ResolveType("System.Int32").WriteTypePrefix(writer);
            writer.Write("* ");
            WriteResultNumber(res);
            writer.WriteLine(", i32 -1");

            opCode.Result = null;
            this.WriteLlvmLoad(opCode, this.ResolveType("System.Int32"), offsetResult);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="classType">
        /// </param>
        /// <param name="interface">
        /// </param>
        /// <exception cref="IndexOutOfRangeException">
        /// </exception>
        private bool WriteInterfaceIndex(LlvmIndentedTextWriter writer, IType classType, IType @interface)
        {
            var type = classType;
            if (!type.HasInterface(@interface))
            {
                return false;
            }

            // first element for pointer (IType* + 0)
            writer.Write(", i32 0");

            while (!type.GetInterfaces().Contains(@interface) || type.BaseType != null && type.BaseType.GetInterfaces().Contains(@interface))
            {
                type = type.BaseType;
                if (type == null)
                {
                    throw new IndexOutOfRangeException("Could not find an interface");
                }

                // first index is base type index
                writer.Write(", i32 0");
            }

            // find index
            var index = 0;

            if (type.IsRootOfVirtualTable())
            {
                index++;
            }

            if (type.BaseType != null)
            {
                index++;
            }

            var found = false;
            foreach (var typeInterface in type.GetInterfaces())
            {
                if (typeInterface.NameEquals(@interface))
                {
                    found = true;
                    break;
                }

                index++;
            }

            if (!found)
            {
                throw new IndexOutOfRangeException("Could not find an interface");
            }

            writer.Write(", i32 ");
            writer.Write(index);

            return true;
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="index">
        /// </param>
        /// <param name="asReference">
        /// </param>
        private void WriteLlvmArgVarAccess(LlvmIndentedTextWriter writer, int index, bool asReference = false)
        {
            this.Parameters[index].ParameterType.WriteTypePrefix(writer, false);
            if (asReference)
            {
                writer.Write('*');
            }

            writer.Write(' ');
            writer.Write(this.GetArgVarName(index));

            // TODO: optional do we need to calculate it propertly?
            writer.Write(", align " + PointerSize);
        }

        /// <summary>
        /// </summary>
        /// <param name="locals">
        /// </param>
        private void WriteLocalVariableDeclarations(IEnumerable<ILocalVariable> locals)
        {
            foreach (var local in locals)
            {
                this.Output.Write(string.Format("%local{0} = ", local.LocalIndex));
                this.WriteAlloca(local.LocalType);
                this.Output.WriteLine(string.Empty);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="endPart">
        /// </param>
        private void WriteMethodBody(bool doNotWrite = false)
        {
            var rest = this.PrepareWritingMethodBody();

            if (doNotWrite)
            {
                return;
            }

            foreach (var opCodePart in rest)
            {
                this.ActualWrite(this.Output, opCodePart, true);
                this.Output.WriteLine(string.Empty);
            }
        }

        /// <summary>
        /// </summary>
        private void WriteMethodNumber()
        {
            // write number of method
            this.Output.Write(" #");
            this.Output.Write(methodNumberIncremental);
        }

        /// <summary>
        /// </summary>
        private void WritePostDeclarations()
        {
            // after writing type you need to generate static members
            foreach (var field in this.staticFieldsInfo)
            {
                this.Output.Write("@\"{0}\" = global ", GetFullFieldName(field));
                field.FieldType.WriteTypePrefix(this.Output, false);
                if (field.FieldType.IsStructureType())
                {
                    this.Output.WriteLine(" zeroinitializer, align 4");
                }
                else
                {
                    this.Output.WriteLine(" undef");
                }
            }

            // write VirtualTable
            if (!this.ThisType.IsInterface)
            {
                if (this.ThisType.HasAnyVirtualMethod())
                {
                    this.Output.WriteLine(string.Empty);
                    this.Output.Write(this.ThisType.GetVirtualTableName());
                    var virtualTable = this.ThisType.GetVirtualTable();
                    virtualTable.WriteTableOfMethods(this, this.ThisType);

                    foreach (var methodInVirtualTable in virtualTable)
                    {
                        CheckIfExternalDeclarationIsRequired(methodInVirtualTable.Value);
                    }
                }

                var index = 1;
                foreach (var @interface in this.ThisType.GetInterfaces())
                {
                    this.Output.WriteLine(string.Empty);
                    this.Output.Write(this.ThisType.GetVirtualInterfaceTableName(@interface));
                    var virtualInterfaceTable = this.ThisType.GetVirtualInterfaceTable(@interface);
                    virtualInterfaceTable.WriteTableOfMethods(this, this.ThisType, index++);
                }
            }
        }

        /// <summary>
        /// </summary>
        private void WriteRequiredDeclarations()
        {
            if (this.typeRttiDeclRequired.Count > 0)
            {
                this.Output.WriteLine(string.Empty);
                foreach (var rttiDecl in this.typeRttiDeclRequired)
                {
                    if (this.processedRttiTypes.Contains(rttiDecl))
                    {
                        continue;
                    }

                    rttiDecl.WriteRttiClassInfoExternalDeclaration(this.Output);
                    this.Output.WriteLine(string.Empty);
                }
            }

            if (this.typeRttiPointerDeclRequired.Count > 0)
            {
                this.Output.WriteLine(string.Empty);
                foreach (var rttiPointerDecl in this.typeRttiPointerDeclRequired)
                {
                    if (this.processedRttiPointerTypes.Contains(rttiPointerDecl))
                    {
                        continue;
                    }

                    rttiPointerDecl.WriteRttiPointerClassInfoExternalDeclaration(this.Output);
                    this.Output.WriteLine(string.Empty);
                }
            }

            if (this.typeDeclRequired.Count > 0)
            {
                this.Output.WriteLine(string.Empty);
                foreach (var opaqueType in this.typeDeclRequired)
                {
                    if (this.processedTypes.Contains(opaqueType) || opaqueType.IsArray)
                    {
                        continue;
                    }

                    opaqueType.UseAsClass = true;
                    this.WriteTypeDeclarationStart(opaqueType);
                    opaqueType.UseAsClass = false;

                    this.Output.WriteLine("opaque");
                }
            }

            if (this.methodDeclRequired.Count > 0)
            {
                this.Output.WriteLine(string.Empty);
                foreach (var externalMethodDecl in this.methodDeclRequired)
                {
                    if (this.processedMethods.Contains(externalMethodDecl))
                    {
                        continue;
                    }

                    this.Output.Write("declare ");

                    var ctor = externalMethodDecl as IConstructor;
                    if (ctor != null)
                    {
                        this.ReadMethodInfo(ctor);
                        this.Output.Write("void ");
                        this.WriteMethodDefinitionName(this.Output, ctor);
                        this.WriteMethodParamsDef(this.Output, ctor.GetParameters(), this.HasMethodThis, this.ThisType, this.ResolveType("System.Void"));
                        this.Output.WriteLine(string.Empty);
                        continue;
                    }

                    var method = externalMethodDecl;
                    if (method != null)
                    {
                        this.ReadMethodInfo(method);
                        this.WriteMethodReturnType(this.Output, method);
                        this.WriteMethodDefinitionName(this.Output, method);
                        this.WriteMethodParamsDef(this.Output, method.GetParameters(), this.HasMethodThis, this.ThisType, method.ReturnType);
                        this.Output.WriteLine(string.Empty);
                        continue;
                    }
                }
            }
        }

        private void WriteUnicodeString(KeyValuePair<int, string> pair)
        {
            this.Output.Write("@.s{0} = private unnamed_addr constant {4} i32, [{2} x i16] {5} {4} i32 {3}, [{2} x i16] [", pair.Key, pair.Value, pair.Value.Length + 1, pair.Value.Length, '{', '}');

            var index = 0;
            foreach (var c in pair.Value.ToCharArray())
            {
                if (index > 0)
                {
                    this.Output.Write(", ");
                }

                this.Output.Write("i16 {0}", (int)c);
                index++;
            }

            if (index > 0)
            {
                this.Output.Write(", ");
            }

            this.Output.WriteLine("i16 0] {0}, align 2", '}');
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void WriteTryBegins(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.TryBegin == null || opCode.TryBegin.Count <= 0)
            {
                return;
            }

            var ehs = opCode.TryBegin.ToArray();
            opCode.TryBegin.Clear();
            Array.Sort(ehs);
            foreach (var eh in ehs.Reverse())
            {
                writer.WriteLine("; Try, start of scope");
                this.tryScopes.Push(eh);
            }
        }

        private void WriteCatchBegins(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.CatchOrFinallyBegin == null)
            {
                return;
            }

            var exceptionHandler = opCode.CatchOrFinallyBegin;
            opCode.CatchOrFinallyBegin = null;
            this.catchScopes.Push(exceptionHandler);

            if (exceptionHandler.Flags == ExceptionHandlingClauseOptions.Clause)
            {
                writer.WriteLine(string.Empty);
                this.WriteCatchTest(
                    exceptionHandler, exceptionHandler.Next);
            }

            writer.WriteLine(string.Empty);

            this.WriteCatchBegin(exceptionHandler);
        }

        /// <summary>
        /// </summary>
        /// <param name="writer">
        /// </param>
        /// <param name="opCode">
        /// </param>
        private void WriteTryEnds(LlvmIndentedTextWriter writer, OpCodePart opCode)
        {
            if (opCode.TryEnd == null)
            {
                return;
            }

            var eh = opCode.TryEnd;
            opCode.TryEnd = null;
            var ehPopped = this.tryScopes.Pop();
            Debug.Assert(ehPopped == eh, "Mismatch of exception handlers");
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        private void WriteTypeDeclarationStart(IType type)
        {
            this.Output.Write("%");

            type.WriteTypeName(this.Output);

            this.Output.Write(" = type ");
        }

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        private void WriteTypeDefinitionIfNotWrittenYet(IType type)
        {
            if (this.processedTypes.Contains(type))
            {
                return;
            }

            Il2Converter.WriteTypeDefinition(this, type, null, true);
            this.Output.WriteLine(string.Empty);
        }

        /// <summary>
        /// </summary>
        [Flags]
        public enum OperandOptions
        {
            /// <summary>
            /// </summary>
            None = 0,

            /// <summary>
            /// </summary>
            GenerateResult = 1,

            /// <summary>
            /// </summary>
            ToFloat = 2,

            /// <summary>
            /// </summary>
            ToInteger = 4,

            /// <summary>
            /// </summary>
            TypeIsInSecondOperand = 8,

            /// <summary>
            /// </summary>
            TypeIsInOperator = 16,

            /// <summary>
            /// </summary>
            NoTypePrefix = 32,

            /// <summary>
            /// </summary>
            AppendPointer = 64,

            /// <summary>
            /// </summary>
            IgnoreOperand = 128,

            /// <summary>
            /// </summary>
            DetectAndWriteTypeInSecondOperand = 256,

            /// <summary>
            /// </summary>
            CastPointersToBytePointer = 512,

            /// <summary>
            /// </summary>
            AdjustIntTypes = 1024,
        }

        /// <summary>
        /// </summary>
        /// <typeparam name="K">
        /// </typeparam>
        /// <typeparam name="V">
        /// </typeparam>
        public class Pair<K, V>
        {
            /// <summary>
            /// </summary>
            public K Key { get; set; }

            /// <summary>
            /// </summary>
            public V Value { get; set; }
        }
    }
}