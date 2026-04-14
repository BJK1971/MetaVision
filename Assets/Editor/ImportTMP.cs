#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;

public static class ImportTMP
{
    [MenuItem("MetaVision/Import TMP Essential Resources")]
    public static void Import()
    {
        // Find the package in the ugui package cache
        var packageDir = Path.GetFullPath("Packages/com.unity.ugui");
        var essentialPath = Path.Combine(packageDir, "Package Resources", "TMP Essential Resources.unitypackage");

        if (!File.Exists(essentialPath))
        {
            // Fallback: search in Library cache
            var cacheDir = "Library/PackageCache";
            foreach (var dir in Directory.GetDirectories(cacheDir, "com.unity.ugui@*"))
            {
                var path = Path.Combine(dir, "Package Resources", "TMP Essential Resources.unitypackage");
                if (File.Exists(path))
                {
                    essentialPath = path;
                    break;
                }
            }
        }

        if (File.Exists(essentialPath))
        {
            AssetDatabase.ImportPackage(essentialPath, false); // false = no dialog, import all
            Debug.Log("[MetaVision] TMP Essential Resources imported!");
        }
        else
        {
            Debug.LogError("[MetaVision] TMP Essential Resources.unitypackage not found!");
        }
    }
}
#endif
