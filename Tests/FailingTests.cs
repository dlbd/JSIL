﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JSIL.Internal;
using NUnit.Framework;

namespace JSIL.Tests {
    [TestFixture]
    public class FailingTests : GenericTestFixture {
        public static Regex MetacommentRegex = new Regex(
            @"//@(?'command'[A-Za-z_0-9]+) (?'arguments'[^\n\r]*)",
            RegexOptions.ExplicitCapture
        );

        [Test]
        [TestCaseSource("FailingTestCasesSource")]
        public void FailingTestCases (object[] parameters) {
            var passed = false;

            var testFilename = (string)parameters[0];
            var translationFailures = new List<Exception>();
            Exception thrown = null;

            try {
                RunSingleComparisonTestCase(
                    parameters, 
                    makeConfiguration: () => {
                        var cfg = MakeConfiguration();
                        cfg.UseThreads = false;
                        return cfg;
                    },
                    onTranslationFailure: (exc) => {
                        lock (translationFailures)
                            translationFailures.Add(exc);
                    }
                );

                passed = true;
            } catch (Exception exc) {
                thrown = exc;
            }

            foreach (var failure in translationFailures)
                Console.WriteLine(failure.ToString());

            var testFileText = File.ReadAllText(testFilename);
            foreach (Match metacommentMatch in MetacommentRegex.Matches(testFileText)) {
                Console.WriteLine(metacommentMatch.Value);

                var command = metacommentMatch.Groups["command"].Value.ToLower();
                var args = metacommentMatch.Groups["arguments"].Value;

                switch (command) {
                    case "assertfailurestring":
                        Assert.IsTrue(
                            translationFailures.Any(
                                (f) => f.ToString().Contains(args)
                            ),
                            "Expected translation to generate a failure containing the string '" + args + "'"
                        );

                        break;

                    case "assertthrows":
                        if ((thrown == null) || (thrown.GetType().Name.ToLower() != args.Trim().ToLower()))
                            Assert.Fail("Expected test to throw an exception of type '" + args + "'");

                        break;

                    default:
                        throw new NotImplementedException("Command type '" + command + "' not supported in metacomments");
                }
            }

            Assert.IsFalse(passed, "Test passed when it should have failed");
        }

        protected IEnumerable<TestCaseData> FailingTestCasesSource () {
            return FolderTestSource("FailingTestCases", MakeDefaultProvider(), new AssemblyCache());
        }
    }
}