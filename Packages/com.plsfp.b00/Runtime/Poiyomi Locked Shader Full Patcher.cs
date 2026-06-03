#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class Poiyomi_Locked_Shader_Full_Patcher
{
    private const string BackupExtension = ".v10backup";

    [MenuItem("Tools/Poiyomi Locked Shader Patcher/1 - Report Safe Known Issues")]
    public static void ReportSafeKnownIssues()
    {
        RunSafePatch(apply:false);
    }

    [MenuItem("Tools/Poiyomi Locked Shader Patcher/2 - Apply Safe Patch")]
    public static void ApplySafePatch()
    {
        bool confirm = EditorUtility.DisplayDialog(
            "Apply safe Poiyomi generated shader patch?",
            "This only edits generated files under OptimizedShaders.\n\n" +
            "Before editing, a .v10backup rollback copy is created automatically.\n\n" +
            "Safe patch includes:\n" +
            "- double-negative constants like --0.1\n" +
            "- const variables reassigned later\n" +
            "- smoothstep(a, a, x) zero-range warnings",
            "Apply Safe Patch",
            "Cancel"
        );

        if (!confirm) return;

        RunSafePatch(apply:true);
    }

    [MenuItem("Tools/Poiyomi Locked Shader Patcher/3 - Roll Back Last Patch")]
    public static void RollbackLastPatch()
    {
        string assetsRoot = Application.dataPath.Replace("\\", "/");
        string[] backups = Directory.GetFiles(assetsRoot, "*" + BackupExtension, SearchOption.AllDirectories);

        int restored = 0;

        foreach (string backupPathRaw in backups)
        {
            string backupPath = backupPathRaw.Replace("\\", "/");
            string originalPath = backupPath.Substring(0, backupPath.Length - BackupExtension.Length);

            if (!File.Exists(backupPath))
                continue;

            File.Copy(backupPath, originalPath, true);
            File.Delete(backupPath);
            restored++;
        }

        AssetDatabase.Refresh();

        Debug.Log(
            "Poiyomi Safe Patcher - rollback complete.\n" +
            "Restored files: " + restored
        );
    }

    private static void RunSafePatch(bool apply)
    {
        string assetsRoot = Application.dataPath.Replace("\\", "/");
        string[] files = Directory.GetFiles(assetsRoot, "*.*", SearchOption.AllDirectories);

        int scanned = 0;
        int filesWithIssues = 0;
        int patchedFiles = 0;

        int doubleNegativeFixes = 0;
        int constFixes = 0;
        int smoothstepFixes = 0;

        StringBuilder log = new StringBuilder();

        foreach (string rawPath in files)
        {
            string path = rawPath.Replace("\\", "/");

            if (!IsGeneratedLockedShaderFile(path))
                continue;

            scanned++;

            string text = File.ReadAllText(path);
            string original = text;

            int localDouble = FixDoubleNegativeConstants(ref text, apply);
            int localConst = FixBadConstAssignments(ref text, apply);
            int localSmooth = FixSameEdgeSmoothstep(ref text, apply);

            int localTotal = localDouble + localConst + localSmooth;

            if (localTotal > 0)
            {
                filesWithIssues++;

                doubleNegativeFixes += localDouble;
                constFixes += localConst;
                smoothstepFixes += localSmooth;

                log.AppendLine(ToAssetsPath(path));

                if (localDouble > 0)
                    log.AppendLine("  double-negative constants: " + localDouble);

                if (localConst > 0)
                    log.AppendLine("  const reassignment risks: " + localConst);

                if (localSmooth > 0)
                    log.AppendLine("  same-edge smoothstep ranges: " + localSmooth);

                log.AppendLine();
            }

            if (apply && text != original)
            {
                string backupPath = path + BackupExtension;

                if (!File.Exists(backupPath))
                    File.WriteAllText(backupPath, original, new UTF8Encoding(false));

                File.WriteAllText(path, text, new UTF8Encoding(false));
                patchedFiles++;
            }
        }

        if (apply)
            AssetDatabase.Refresh();

        Debug.Log(
            "Poiyomi Safe Patcher\n" +
            "Mode: " + (apply ? "ApplySafePatch" : "ReportOnly") + "\n" +
            "Scanned generated files: " + scanned + "\n" +
            "Files with known safe issues: " + filesWithIssues + "\n" +
            "Files patched: " + patchedFiles + "\n\n" +
            "Totals:\n" +
            "- double-negative constants: " + doubleNegativeFixes + "\n" +
            "- const reassignment risks: " + constFixes + "\n" +
            "- same-edge smoothstep ranges: " + smoothstepFixes + "\n\n" +
            log.ToString()
        );
    }

    private static bool IsGeneratedLockedShaderFile(string path)
    {
        if (!path.Contains("/OptimizedShaders/"))
            return false;

        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext == ".cginc" || ext == ".shader" || ext == ".hlsl" || ext == ".glslinc";
    }

    private static int FixDoubleNegativeConstants(ref string text, bool apply)
    {
        Regex rx = new Regex(@"--(?<num>(?:\d+\.\d+|\.\d+|\d+)(?:[fFhH])?)", RegexOptions.Compiled);

        if (!apply)
            return rx.Matches(text).Count;

        int count = 0;

        text = rx.Replace(text, m =>
        {
            count++;
            return "(-" + m.Groups["num"].Value + ")";
        });

        return count;
    }

    private static int FixBadConstAssignments(ref string text, bool apply)
    {
        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        string[] lines = normalized.Split('\n');

        bool changed = false;
        int count = 0;

        Regex declaration = new Regex(
            @"^(?<indent>\s*)const\s+(?<type>(?:half|float|fixed|int|uint|bool)(?:[1-4](?:x[1-4])?)?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=",
            RegexOptions.Compiled
        );

        for (int i = 0; i < lines.Length; i++)
        {
            Match m = declaration.Match(lines[i]);

            if (!m.Success)
                continue;

            string name = m.Groups["name"].Value;

            Regex laterAssignment = new Regex(
                @"(^|[^\w])" + Regex.Escape(name) + @"\s*(?:=|\+=|-=|\*=|/=|%=)",
                RegexOptions.Compiled
            );

            bool assignedLater = false;

            for (int j = i + 1; j < lines.Length; j++)
            {
                string trimmed = lines[j].TrimStart();

                if (trimmed.StartsWith("//"))
                    continue;

                if (laterAssignment.IsMatch(lines[j]))
                {
                    assignedLater = true;
                    break;
                }
            }

            if (!assignedLater)
                continue;

            count++;

            if (apply)
            {
                lines[i] = lines[i].Replace(
                    "const " + m.Groups["type"].Value,
                    m.Groups["type"].Value
                );

                changed = true;
            }
        }

        if (apply && changed)
            text = string.Join("\n", lines);

        return count;
    }

    private static int FixSameEdgeSmoothstep(ref string text, bool apply)
    {
        Regex rx = new Regex(
            @"smoothstep\s*\(\s*(?<a>-?(?:\d+\.\d+|\.\d+|\d+)(?:[fFhH])?)\s*,\s*\k<a>\s*,",
            RegexOptions.Compiled
        );

        if (!apply)
            return rx.Matches(text).Count;

        int count = 0;

        text = rx.Replace(text, m =>
        {
            string aOriginal = m.Groups["a"].Value;
            string aRaw = aOriginal;
            string suffix = "";

            if (aRaw.EndsWith("f", StringComparison.OrdinalIgnoreCase) ||
                aRaw.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                suffix = aRaw.Substring(aRaw.Length - 1);
                aRaw = aRaw.Substring(0, aRaw.Length - 1);
            }

            double a;

            if (!double.TryParse(
                aRaw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out a))
            {
                return m.Value;
            }

            count++;

            double b = a + 0.0001;

            string bText = b.ToString(
                "0.#######",
                System.Globalization.CultureInfo.InvariantCulture
            ) + suffix;

            return "smoothstep(" + aOriginal + ", " + bText + ",";
        });

        return count;
    }

    private static string ToAssetsPath(string absolutePath)
    {
        string p = absolutePath.Replace("\\", "/");
        string root = Application.dataPath.Replace("\\", "/");

        if (p.StartsWith(root))
            return "Assets" + p.Substring(root.Length);

        return p;
    }
}
#endif
