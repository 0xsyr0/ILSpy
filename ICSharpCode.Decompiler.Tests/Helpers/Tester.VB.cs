using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.VisualBasic;

using NUnit.Framework;

namespace ICSharpCode.Decompiler.Tests.Helpers
{
	partial class Tester
	{
		public static CompilerResults CompileVB(string sourceFileName, CompilerOptions flags = CompilerOptions.UseDebug, string outputFileName = null)
		{
			List<string> sourceFileNames = new List<string> { sourceFileName };
			foreach (Match match in Regex.Matches(File.ReadAllText(sourceFileName), @"#include ""([\w\d./]+)"""))
			{
				sourceFileNames.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFileName), match.Groups[1].Value)));
			}

			var preprocessorSymbols = GetPreprocessorSymbols(flags).Select(symbol => new KeyValuePair<string, object>(symbol, 1)).ToList();

			if ((flags & CompilerOptions.UseRoslynMask) != 0)
			{
				CompilerResults results = new CompilerResults(new TempFileCollection());
				results.PathToAssembly = outputFileName ?? Path.GetTempFileName();

				var (roslynVersion, languageVersion) = (flags & CompilerOptions.UseRoslynMask) switch
				{
					CompilerOptions.UseRoslyn1_3_2 => ("1.3.2", "14"),
					CompilerOptions.UseRoslyn2_10_0 => ("2.10.0", "latest"),
					_ => (RoslynLatestVersion, flags.HasFlag(CompilerOptions.Preview) ? "preview" : "latest")
				};

				var vbcPath = roslynToolset.GetVBCompiler(roslynVersion);

				IEnumerable<string> references;
				if (flags.HasFlag(CompilerOptions.ReferenceCore))
				{
					references = coreDefaultReferences.Value.Select(r => "-r:\"" + r + "\"");
				}
				else
				{
					references = defaultReferences.Value.Select(r => "-r:\"" + r + "\"");
				}
				if (flags.HasFlag(CompilerOptions.ReferenceVisualBasic))
				{
					references = references.Concat(visualBasic.Value.Select(r => "-r:\"" + r + "\""));
				}
				// note: the /shared switch is undocumented. It allows us to use the VBCSCompiler.exe compiler
				// server to speed up testing.
				string otherOptions = $"/shared -noconfig " +
					$"-optioninfer+ -optionexplicit+ " +
					$"-langversion:{languageVersion} " +
					$"/optimize{(flags.HasFlag(CompilerOptions.Optimize) ? "+ " : "- ")}";

				if (flags.HasFlag(CompilerOptions.Library))
				{
					otherOptions += "-t:library ";
				}
				else
				{
					otherOptions += "-t:exe ";
				}

				if (flags.HasFlag(CompilerOptions.GeneratePdb))
				{
					otherOptions += "-debug:full ";
				}
				else
				{
					otherOptions += "-debug- ";
				}

				if (flags.HasFlag(CompilerOptions.Force32Bit))
				{
					otherOptions += "-platform:x86 ";
				}
				else
				{
					otherOptions += "-platform:anycpu ";
				}
				if (preprocessorSymbols.Count > 0)
				{
					otherOptions += " \"-d:" + string.Join(",", preprocessorSymbols.Select(kv => kv.Key + "=" + kv.Value)) + "\" ";
				}

				ProcessStartInfo info = new ProcessStartInfo(vbcPath);
				info.Arguments = $"{otherOptions}{string.Join(" ", references)} -out:\"{Path.GetFullPath(results.PathToAssembly)}\" {string.Join(" ", sourceFileNames.Select(fn => '"' + Path.GetFullPath(fn) + '"'))}";
				info.RedirectStandardError = true;
				info.RedirectStandardOutput = true;
				info.UseShellExecute = false;

				Console.WriteLine($"\"{info.FileName}\" {info.Arguments}");

				Process process = Process.Start(info);

				var outputTask = process.StandardOutput.ReadToEndAsync();
				var errorTask = process.StandardError.ReadToEndAsync();

				Task.WaitAll(outputTask, errorTask);
				process.WaitForExit();

				Console.WriteLine("output: " + outputTask.Result);
				Console.WriteLine("errors: " + errorTask.Result);
				Assert.AreEqual(0, process.ExitCode, "vbc failed");
				return results;
			}
			else if (flags.HasFlag(CompilerOptions.UseMcs))
			{
				throw new NotSupportedException("Cannot use mcs for VB");
			}
			else
			{
				var provider = new VBCodeProvider(new Dictionary<string, string> { { "CompilerVersion", "v4.0" } });
				CompilerParameters options = new CompilerParameters();
				options.GenerateExecutable = !flags.HasFlag(CompilerOptions.Library);
				options.CompilerOptions = "/optimize" + (flags.HasFlag(CompilerOptions.Optimize) ? "+" : "-");
				options.CompilerOptions += (flags.HasFlag(CompilerOptions.UseDebug) ? " /debug" : "");
				options.CompilerOptions += (flags.HasFlag(CompilerOptions.Force32Bit) ? " /platform:anycpu32bitpreferred" : "");
				options.CompilerOptions += " /optioninfer+ /optionexplicit+";
				if (preprocessorSymbols.Count > 0)
				{
					options.CompilerOptions += " /d:" + string.Join(",", preprocessorSymbols.Select(p => $"{p.Key}={p.Value}"));
				}
				if (outputFileName != null)
				{
					options.OutputAssembly = outputFileName;
				}

				options.ReferencedAssemblies.Add("System.dll");
				options.ReferencedAssemblies.Add("System.Core.dll");
				options.ReferencedAssemblies.Add("System.Xml.dll");
				if (flags.HasFlag(CompilerOptions.ReferenceVisualBasic))
				{
					options.ReferencedAssemblies.Add("Microsoft.VisualBasic.dll");
				}
				CompilerResults results = provider.CompileAssemblyFromFile(options, sourceFileNames.ToArray());
				if (results.Errors.Cast<CompilerError>().Any(e => !e.IsWarning))
				{
					StringBuilder b = new StringBuilder("Compiler error:");
					foreach (var error in results.Errors)
					{
						b.AppendLine(error.ToString());
					}
					throw new Exception(b.ToString());
				}
				return results;
			}
		}
	}
}
