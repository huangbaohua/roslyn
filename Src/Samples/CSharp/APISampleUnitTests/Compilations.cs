// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Emit;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISampleUnitTestsCS
{
    [TestClass]
    public class Compilations
    {
        [TestMethod]
        public void EndToEndCompileAndRun()
        {
            var expression = "6 * 7";
            var text = @"public class Calculator
{
    public static object Evaluate()
    {
        return $;
    } 
}".Replace("$", expression);

            var tree = SyntaxFactory.ParseSyntaxTree(text);
            var compilation = CSharpCompilation.Create(
                "calc.dll",
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                syntaxTrees: new[] { tree },
                references: new[] { MetadataReference.CreateFromAssembly(typeof(object).Assembly) });

            Assembly compiledAssembly;
            using (var stream = new MemoryStream())
            {
                var compileResult = compilation.Emit(stream);
                compiledAssembly = Assembly.Load(stream.GetBuffer());
            }

            Type calculator = compiledAssembly.GetType("Calculator");
            MethodInfo evaluate = calculator.GetMethod("Evaluate");
            string answer = evaluate.Invoke(null, null).ToString();

            Assert.AreEqual("42", answer);
        }

        [TestMethod]
        public void GetErrorsAndWarnings()
        {
            string text = @"class Program
{
    static int Main(string[] args)
    {
    }
}";

            var tree = SyntaxFactory.ParseSyntaxTree(text);
            var compilation = CSharpCompilation
                .Create("program.exe")
                .AddSyntaxTrees(tree)
                .AddReferences(MetadataReference.CreateFromAssembly(typeof(object).Assembly));

            IEnumerable<Diagnostic> errorsAndWarnings = compilation.GetDiagnostics();
            Assert.AreEqual(1, errorsAndWarnings.Count());

            Diagnostic error = errorsAndWarnings.First();
            Assert.AreEqual(
                "'Program.Main(string[])': not all code paths return a value",
                error.GetMessage(CultureInfo.InvariantCulture));

            Location errorLocation = error.Location;
            Assert.AreEqual(4, error.Location.SourceSpan.Length);

            SourceText programText = errorLocation.SourceTree.GetText();
            Assert.AreEqual("Main", programText.ToString(errorLocation.SourceSpan));

            FileLinePositionSpan span = error.Location.GetLineSpan();
            Assert.AreEqual(15, span.StartLinePosition.Character);
            Assert.AreEqual(2, span.StartLinePosition.Line);
        }
    }
}
