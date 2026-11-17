using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Hexfront.EditorTools
{
    /// <summary>
    /// Runs when the project is opened in the Unity Editor. The game builds itself from code at
    /// runtime, so all the project needs is one (empty) scene in the build list and a few player
    /// settings. This creates them once, so there is nothing to set up by hand.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectSetup
    {
        const string ScenePath = "Assets/Scenes/Main.unity";

        static ProjectSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(ScenePath) && !EditorApplication.isPlayingOrWillChangePlaymode) Run();
            };
        }

        [MenuItem("Hexfront/Set Up Project")]
        public static void Run()
        {
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory("Assets/Scenes");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.Refresh();
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            PlayerSettings.productName = "HEXFRONT";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            // LAN play uses sockets, so Android must always request the INTERNET permission.
            PlayerSettings.Android.forceInternetPermission = true;
            AssetDatabase.SaveAssets();
            Debug.Log("HEXFRONT: project set up. Press Play to start (scene: " + ScenePath + ").");
        }
    }
}
