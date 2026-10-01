using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpParrelSyncValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<object> Checks = new List<object>();
    private static string Root;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Invoke<T>(string method, string path) => (T)typeof(MCPInstanceRegistry).GetMethod(method, Hidden, null, new[] { typeof(string) }, null).Invoke(null, new object[] { path });
    private static string Project(string name, bool marker = false)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(path, "Assets"));
        Directory.CreateDirectory(Path.Combine(path, "ProjectSettings"));
        File.WriteAllText(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.6.2f1");
        if (marker) File.WriteAllText(Path.Combine(path, ".clone"), "");
        return path;
    }
    private static void Verify(string path, bool clone, int index, string original)
    {
        Require(Invoke<bool>("IsParrelSyncClonePath", path) == clone, "Marker classification mismatch");
        Require(Invoke<int>("GetParrelSyncCloneIndex", path) == index, "Clone index mismatch");
        Require(Invoke<string>("GetParrelSyncOriginalProjectPath", path).Replace('\\', '/') == original.Replace('\\', '/'), "Original project mismatch");
    }
    private static void Check(string name, Action action)
    {
        try { action(); Checks.Add(new { name, passed = true }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
    }
    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        string library = Path.GetFullPath("Library");
        Root = Path.Combine(library, "ParrelIdentity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        try
        {
            string main = Project("Main"), namedMain = Project("Main_clone_7");
            Check("An ordinary project remains its own main project", () => Verify(main, false, -1, main));
            Check("A clone-shaped folder without the native marker remains a main project", () => Verify(namedMain, false, -1, namedMain));
            Check("A native marked numeric clone reports its original project", () => Verify(Project("Main_clone_0", true), true, 0, main));
            Check("The final suffix resolves a parent whose own name contains clone text", () => Verify(Project("Main_clone_7_clone_2", true), true, 2, namedMain));
            Check("A renamed marked clone keeps identity with unknown index and parent", () => Verify(Project("RenamedPlayer", true), true, -1, ""));
            Check("An orphaned marked clone does not invent a missing source project", () => Verify(Project("Absent_clone_3", true), true, 3, ""));
            Check("An overflowing suffix keeps clone identity without an invalid index", () => Verify(Project("Main_clone_99999999999999999999", true), true, -1, main));
            Check("A nonnumeric native suffix can retain a known original project", () => Verify(Project("Main_clone_client", true), true, -1, main));
            Check("Trailing directory separators preserve identity", () => Verify(Project("Main_clone_4", true) + Path.DirectorySeparatorChar, true, 4, main));
            Check("A marker directory is not the native marker file", () => {
                string path = Project("Main_clone_5"); Directory.CreateDirectory(Path.Combine(path, ".clone")); Verify(path, false, -1, path);
            });
            Check("A plain directory cannot be inferred as a Unity source project", () => {
                Directory.CreateDirectory(Path.Combine(Root, "Plain")); Verify(Project("Plain_clone_0", true), true, 0, "");
            });
            Check("Marker creation and removal are observed without stale identity", () => {
                string path = Project("Main_clone_6"); Verify(path, false, -1, path);
                File.WriteAllText(Path.Combine(path, ".clone"), ""); Verify(path, true, 6, main);
                File.Delete(Path.Combine(path, ".clone")); Verify(path, false, -1, path);
            });
        }
        finally
        {
            string resolved = Path.GetFullPath(Root);
            Require(Path.GetDirectoryName(resolved) == library && Path.GetFileName(resolved).StartsWith("ParrelIdentity-"), "Unexpected cleanup root");
            Directory.Delete(resolved, true);
        }
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpParrelSyncValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
