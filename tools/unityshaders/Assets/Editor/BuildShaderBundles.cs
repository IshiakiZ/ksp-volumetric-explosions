using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Volumetric Explosions shaders into one asset bundle for each kind of computer the game runs
/// on. Run from the command line (see tools/unityshaders/README.md), or from the menu in the editor.
/// The game is Unity 2019.4.18f1 and loads only bundles made by that version.
/// </summary>
public static class BuildShaderBundles
{
    [MenuItem("Volumetric Explosions/Build shader bundles")]
    public static void All()
    {
        var build = new AssetBundleBuild
        {
            assetBundleName = "shaders",
            assetNames = new[] { "Assets/VolumetricExplosions/Volume.shader", "Assets/VolumetricExplosions/Mark.shader" },
        };
        Build(build, BuildTarget.StandaloneWindows64, "windows");
        Build(build, BuildTarget.StandaloneLinux64, "linux");
        Build(build, BuildTarget.StandaloneOSX, "mac");
    }

    static void Build(AssetBundleBuild build, BuildTarget target, string name)
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
        {
            Debug.Log("skipped " + name + ": this editor has no build support for it installed");
            return;
        }
        string folder = "Bundles/" + name;
        Directory.CreateDirectory(folder);
        BuildPipeline.BuildAssetBundles(folder, new[] { build }, BuildAssetBundleOptions.ForceRebuildAssetBundle, target);
        string made = folder + "/shaders", wanted = "Bundles/shaders-" + name + ".bundle";
        if (File.Exists(made)) { File.Copy(made, wanted, true); Debug.Log("wrote " + wanted); }
        else Debug.LogError("no bundle was made for " + name);
    }
}
