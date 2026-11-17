#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace Hexfront.EditorTools
{
    /// <summary>
    /// iOS shows a "find devices on your local network" permission prompt, and requires the app
    /// to explain why. This adds that explanation to the generated Xcode project's Info.plist.
    /// </summary>
    public static class IosPostBuild
    {
        [PostProcessBuild]
        public static void OnPostProcessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetString("NSLocalNetworkUsageDescription",
                "HEXFRONT uses your local Wi-Fi network to find and play against a friend nearby.");
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
