﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ThreadGen.cs" company="">
//   
// </copyright>
// <summary>
//   
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Il2Native.Logic.Gencode
{
    using System.Collections.Generic;
    using CodeParts;
    using PEAssemblyReader;

    /// <summary>
    /// </summary>
    public static class MonitorGen
    {
        /// <summary>
        /// </summary>
        /// <param name="method">
        /// </param>
        /// <returns>
        /// </returns>
        public static bool IsMonitorFunction(this IMethod method, CWriter cWriter)
        {
            if (!method.IsStatic)
            {
                return false;
            }

            if (method.DeclaringType == null || method.DeclaringType.FullName != "System.Threading.Monitor")
            {
                return false;
            }

            switch (method.MetadataName)
            {
                case "GetMutexAddress":
                case "GetCondAddress":
                    return true;
            }

            return false;
        }

        /// <summary>
        /// </summary>
        /// <param name="method">
        /// </param>
        /// <param name="opCodeMethodInfo">
        /// </param>
        /// <param name="cWriter">
        /// </param>
        public static void WriteMonitorFunction(
            this IMethod method,
            OpCodePart opCodeMethodInfo,
            CWriter cWriter)
        {
            var writer = cWriter.Output;

            switch (method.MetadataName)
            {
                case "GetMutexAddress":

                    if (cWriter.MultiThreadingSupport)
                    {
                        writer.Write("__get_mutex_address((Void*)");
                        cWriter.Peek();
                        writer.Write(")");
                    }
                    else
                    {
                        var estimatedResult = cWriter.EstimatedResultOf(opCodeMethodInfo.OpCodeOperands[0]);
                        writer.Write("__null_address((Void*)");
                        cWriter.Peek();
                        writer.Write(")");
                    }

                    break;

                case "GetCondAddress":

                    if (cWriter.MultiThreadingSupport)
                    {
                        writer.Write("__get_cond_address((Void*)");
                        cWriter.Peek();
                        writer.Write(")");
                    }
                    else
                    {
                        writer.Write("__null_address((Void*)");
                        cWriter.Peek();
                        writer.Write(")");                        
                    }

                    break;
            }
        }
    }
}