// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.CodeAnalysis.Options;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ICSharpCode.NRefactory6.CSharp, PublicKey=00240000048000009400000006020000002400005253413100040000010001004dcf3979c4e902efa4dd2163a039701ed5822e6f1134d77737296abbb97bf0803083cfb2117b4f5446a217782f5c7c634f9fe1fc60b4c11d62c5b3d33545036706296d31903ddcf750875db38a8ac379512f51620bb948c94d0831125fbc5fe63707cbb93f48c1459c4d1749eb7ac5e681a2f0d6d7c60fa527a3c0b8f92b02bf")]

namespace Microsoft.CodeAnalysis.Internal.Log
{
    /// <summary>
    /// Implementation of <see cref="ILogger"/> that produce timing debug output. 
    /// </summary>
    [ExcludeFromCodeCoverage]
    internal sealed class TraceLogger : ILogger
    {
        public static readonly TraceLogger Instance = new TraceLogger();

        private readonly Func<FunctionId, bool> loggingChecker;

        public TraceLogger()
            : this((Func<FunctionId, bool>)null)
        {
        }

        public TraceLogger(IOptionService optionService)
            : this(Logger.GetLoggingChecker(optionService))
        {
        }

        public TraceLogger(Func<FunctionId, bool> loggingChecker)
        {
            this.loggingChecker = loggingChecker;
        }

        public bool IsEnabled(FunctionId functionId)
        {
            return this.loggingChecker == null || this.loggingChecker(functionId);
        }

        public void Log(FunctionId functionId, LogMessage logMessage)
        {
            Trace.WriteLine(string.Format("[{0}] {1}/{2} - {3}", Thread.CurrentThread.ManagedThreadId, functionId.ToString(), logMessage.GetMessage()));
        }

        public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int uniquePairId, CancellationToken cancellationToken)
        {
            Trace.WriteLine(string.Format("[{0}] Start({1}) : {2}/{3} - {4}", Thread.CurrentThread.ManagedThreadId, uniquePairId, functionId.ToString(), logMessage.GetMessage()));
        }

        public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int uniquePairId, int delta, CancellationToken cancellationToken)
        {
            var functionString = functionId.ToString() + (cancellationToken.IsCancellationRequested ? " Canceled" : string.Empty);
            Trace.WriteLine(string.Format("[{0}] End({1}) : [{2}ms] {3}/{4}", Thread.CurrentThread.ManagedThreadId, uniquePairId, delta, functionString));
        }
    }
}
