﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ICodeWriter.cs" company="">
//   
// </copyright>
// <summary>
//   
// </summary>
// --------------------------------------------------------------------------------------------------------------------
namespace Il2Native.Logic
{
    using Il2Native.Logic.CodeParts;

    using PEAssemblyReader;

    /// <summary>
    /// </summary>
    public interface ICodeWriter
    {
        /// <summary>
        /// </summary>
        void Close();

        /// <summary>
        /// </summary>
        /// <param name="rawText">
        /// </param>
        void Write(string rawText);

        /// <summary>
        /// </summary>
        /// <param name="ilCode">
        /// </param>
        void Write(OpCodePart ilCode, bool doNotWrite = false);

        /// <summary>
        /// </summary>
        void WriteAfterConstructors();

        /// <summary>
        /// </summary>
        /// <param name="count">
        /// </param>
        /// <param name="disablePostDeclarations">
        /// </param>
        void WriteAfterFields(int count, bool disablePostDeclarations = false);

        /// <summary>
        /// </summary>
        void WriteAfterMethods();

        /// <summary>
        /// </summary>
        void WriteBeforeConstructors();

        /// <summary>
        /// </summary>
        /// <param name="count">
        /// </param>
        void WriteBeforeFields(int count);

        /// <summary>
        /// </summary>
        void WriteBeforeMethods();

        /// <summary>
        /// </summary>
        /// <param name="ctor">
        /// </param>
        void WriteConstructorEnd(IConstructor ctor, bool doNotWrite = false);

        /// <summary>
        /// </summary>
        /// <param name="ctor">
        /// </param>
        void WriteConstructorStart(IConstructor ctor, bool doNotWrite = false);

        /// <summary>
        /// </summary>
        void WriteEnd();

        /// <summary>
        /// </summary>
        /// <param name="field">
        /// </param>
        /// <param name="number">
        /// </param>
        /// <param name="count">
        /// </param>
        void WriteFieldEnd(IField field, int number, int count);

        /// <summary>
        /// </summary>
        /// <param name="field">
        /// </param>
        /// <param name="number">
        /// </param>
        /// <param name="count">
        /// </param>
        void WriteFieldStart(IField field, int number, int count);

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        /// <param name="number">
        /// </param>
        /// <param name="count">
        /// </param>
        void WriteForwardDeclaration(IType type, int number, int count);

        /// <summary>
        /// </summary>
        /// <param name="method">
        /// </param>
        void WriteMethodEnd(IMethod method, bool doNotWrite = false);

        /// <summary>
        /// </summary>
        /// <param name="method">
        /// </param>
        void WriteMethodStart(IMethod method, bool doNotWrite = false);

        /// <summary>
        /// </summary>
        /// <param name="moduleName">
        /// </param>
        /// <param name="assemblyName">
        /// </param>/// 
        void WriteStart(string moduleName, string assemblyName);

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        void WriteTypeEnd(IType type);

        /// <summary>
        /// </summary>
        /// <param name="type">
        /// </param>
        /// <param name="genericType">
        /// </param>
        void WriteTypeStart(IType type, IType genericType);
    }
}