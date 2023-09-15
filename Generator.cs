﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BuildInfo.Generator
{
    [Generator]
    public class BuildInfoGenerator : ISourceGenerator
    {
        private static string _cachedFullHash;
        private static string _cachedAbbrevHash;

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new FilterBuildInfoReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var syntaxReceiver = (FilterBuildInfoReceiver)context.SyntaxReceiver;
            var pathToFile     = syntaxReceiver.ClassToAugment.SyntaxTree.FilePath;
            var folder         = Path.GetDirectoryName(pathToFile);
            context.AddSource($"Build.Info.g.cs", SourceText.From(BuildBuildInfo(folder), Encoding.UTF8));
        }

        private string BuildBuildInfo(string folder)
        {
            const string template =
                @"///////////////////////////////////////////////////////////////////
// This file is autogenerated and any changes will be OVERWRITTEN! 
///////////////////////////////////////////////////////////////////

using System;
using System.Reflection;

namespace Build
{
    public static partial class Info
    {
        private const long              BUILD_DATE_BINARY_UTC       = 0x$(BUILD_DATE_BINARY_UTC);    // $(BUILD_DATE_UTC)

        public static AssemblyName      BuildAssemblyName { get; }  = Assembly.GetExecutingAssembly().GetName();
        public static DateTimeOffset    BuildDateUtc { get; }       = DateTime.FromBinary(BUILD_DATE_BINARY_UTC);
        public static string            ModuleText { get; }         = BuildAssemblyName.Name;
        public static string            CommitHash { get; }         = '$(CommitHashFull)';
        public static string            CommitHashAbbrev { get; }   = '$(CommitHashAbbrev)';
        public static string            VersionText { get; }        = 'v' + BuildAssemblyName.Version.ToString()
                                                                                + '.' + CommitHashAbbrev
#if DEBUG
                                                                                + ' [DEBUG]'
#endif
                                                                                ;

        public static string            BuildDateText { get; }      = '$(BUILD_DATE_UTC)';
        public static string            DisplayText { get; }        = $'{ModuleText} {VersionText} (Build Date: {BuildDateText})';
    }
}";

            _cachedFullHash   ??= ReadCached(folder, "build-info-hash-full.hash",   () => RunGit(GIT_CMD_BUILD_HASH, folder));
            _cachedAbbrevHash ??= ReadCached(folder, "build-info-hash-abbrev.hash", () => RunGit(GIT_CMD_BUILD_HASH_ABBREV, folder));

            return template.Replace("$(BUILD_DATE_BINARY_UTC)", DateTimeOffset.UtcNow.DateTime.ToBinary().ToString("x16"))
                           .Replace("$(BUILD_DATE_UTC)", DateTimeOffset.UtcNow.ToString("u"))
                           .Replace("$(CommitHashFull)", _cachedFullHash)
                           .Replace("$(CommitHashAbbrev)", _cachedAbbrevHash)
                           .Replace('\'', '"');
        }

        private static string ReadCached(string workDir, string cacheName, Func<string> generate )
        {
            var file   = Path.Combine(Path.GetTempPath(), $"{workDir.Replace("/", "_").Replace("\\","_")}.{cacheName}");
            string val = null;

            if (File.Exists(file))
            {
                try
                {
                    val = File.ReadAllText(file);
                }
                catch
                {
                    val = null;
                }
            }

            if (string.IsNullOrEmpty(val))
            {
                val = generate();
                File.WriteAllText(file, val);
            }

            return val;
        }


        const string GIT_CMD_BUILD_HASH        = "rev-parse HEAD";
        const string GIT_CMD_BUILD_HASH_ABBREV = "describe --always";

        private static string RunGit(string command, string workDir)
        {
            using (var proc = new Process())
            {
                proc.StartInfo.FileName               = "git";
                proc.StartInfo.Arguments              = command;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.UseShellExecute        = false;
                proc.StartInfo.CreateNoWindow         = true;
                proc.StartInfo.WorkingDirectory       = workDir;

                try
                {
                    _ = proc.Start();
                    var git_out = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    return git_out.Replace("\r\n", "").Replace("\n", "").Replace("\r", "");
                }
                catch (Exception)
                {
                    return "ERROR";
                }
            }
        }

        private class FilterBuildInfoReceiver : ISyntaxReceiver
        {
            public ClassDeclarationSyntax ClassToAugment { get; private set; }

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                // Try to find a the partial class in the format:
                // namespace Build;
                // public partial class Info { }

                if (syntaxNode is ClassDeclarationSyntax cds && cds.Identifier.ValueText == "Info")
                {
                    if (!TryGetParentSyntax(cds, out NamespaceDeclarationSyntax namespaceDeclarationSyntax))
                    {
                        return;
                    }

                    if (namespaceDeclarationSyntax.Name.ToString() == "Build")
                    {
                        ClassToAugment = cds;
                    }
                }
            }

            public static bool TryGetParentSyntax<T>(SyntaxNode syntaxNode, out T result) where T : SyntaxNode
            {
                result = null;

                if (syntaxNode is null)
                {
                    return false;
                }

                try
                {
                    syntaxNode = syntaxNode.Parent;

                    if (syntaxNode is null)
                    {
                        return false;
                    }

                    if (syntaxNode.GetType() == typeof(T))
                    {
                        result = syntaxNode as T;
                        return true;
                    }

                    return TryGetParentSyntax<T>(syntaxNode, out result);
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}