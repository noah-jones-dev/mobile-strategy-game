using System;
using System.Collections.Generic;
using System.IO;
using Hexfront.Core;
using UnityEngine;

namespace Hexfront
{
    public enum MatchMode : byte { VsAi, PassAndPlay, LanHost, LanClient }

    public sealed class SavedMatch
    {
        public MatchMode Mode;
        public AiDifficulty Ai;
        public int TurnTimer;
        public GameState State;
    }

    public sealed class SavedMap
    {
        public string Name, Code;
        public bool Favorite;
    }

    /// <summary>
    /// Local files only (no accounts, no cloud): one auto-saved match and the list of saved maps,
    /// both in Application.persistentDataPath.
    /// </summary>
    public static class SaveSystem
    {
        const int Magic = 0x48584631; // "HXF1"
        static string MatchPath => Path.Combine(Application.persistentDataPath, "match.hexfront");
        static string MapsPath => Path.Combine(Application.persistentDataPath, "maps.txt");

        public static bool HasMatch => File.Exists(MatchPath);

        public static void SaveMatch(MatchMode mode, AiDifficulty ai, int turnTimer, GameState state)
        {
            try
            {
                byte[] data = state.Serialize();
                string tmp = MatchPath + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(Magic); w.Write((byte)mode); w.Write((byte)ai); w.Write(turnTimer);
                    w.Write(data.Length); w.Write(data);
                }
                // Write to a temp file first so a crash mid-save can never corrupt the real one.
                if (File.Exists(MatchPath)) File.Delete(MatchPath);
                File.Move(tmp, MatchPath);
            }
            catch (Exception e) { Debug.LogWarning("Could not save match: " + e.Message); }
        }

        public static SavedMatch LoadMatch()
        {
            try
            {
                using (var r = new BinaryReader(File.OpenRead(MatchPath)))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var m = new SavedMatch { Mode = (MatchMode)r.ReadByte(), Ai = (AiDifficulty)r.ReadByte(), TurnTimer = r.ReadInt32() };
                    int length = r.ReadInt32();
                    m.State = GameState.Deserialize(r.ReadBytes(length));
                    return m;
                }
            }
            catch (Exception e) { Debug.LogWarning("Could not load match: " + e.Message); return null; }
        }

        public static void DeleteMatch()
        {
            try { if (File.Exists(MatchPath)) File.Delete(MatchPath); } catch (Exception) { }
        }

        public static List<SavedMap> LoadMaps()
        {
            var list = new List<SavedMap>();
            try
            {
                if (!File.Exists(MapsPath)) return list;
                foreach (string line in File.ReadAllLines(MapsPath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 3 || !MapConfig.TryParse(parts[1], out _)) continue;
                    list.Add(new SavedMap { Favorite = parts[0] == "1", Code = parts[1], Name = parts[2] });
                }
            }
            catch (Exception e) { Debug.LogWarning("Could not load maps: " + e.Message); }
            return list;
        }

        public static void SaveMaps(List<SavedMap> maps)
        {
            try
            {
                var lines = new List<string>();
                foreach (var m in maps) lines.Add((m.Favorite ? "1" : "0") + "\t" + m.Code + "\t" + (m.Name ?? "").Replace('\t', ' ').Replace('\n', ' '));
                File.WriteAllLines(MapsPath, lines.ToArray());
            }
            catch (Exception e) { Debug.LogWarning("Could not save maps: " + e.Message); }
        }
    }
}
