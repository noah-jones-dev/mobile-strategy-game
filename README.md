# HEXFRONT

A simultaneous-turn, hex-grid military strategy game for iOS and Android, built for **Unity 6.5** (6000.5.x) in C#.
Two players plan in secret, lock in, and watch both plans execute at once. Play over local Wi-Fi, against an AI, or pass-and-play on one device.

## Opening the project

1. In Unity Hub choose **Add > Add project from disk** and pick this folder. If Hub says the exact editor patch is missing, pick your installed 6000.5.x version.
2. On first open an editor script (`Hexfront > Set Up Project`) creates `Assets/Scenes/Main.unity`, adds it to the build list and sets landscape orientation. Nothing else needs wiring.
3. If Unity asks to **enable the Input System backend**, either answer works: the input code supports both the new Input System and the legacy Input Manager.
4. Press **Play**.

Everything on screen (terrain, models, UI, sounds) is generated from code, so there are no art assets, prefabs or scene objects to maintain.

Editor controls: left-drag pan, mouse wheel zoom, right-drag or Q/E rotate, click to select and order.

### Building for phones
- **Android:** switch platform and build. The setup script already forces the INTERNET permission needed for LAN play.
- **iOS:** a post-build step adds the "local network" permission text. iOS additionally restricts UDP broadcast unless the app has Apple's *multicast networking* entitlement, so lobby auto-discovery may not work on iOS until you request it from Apple. **Join by address** (the host screen shows its IP) always works.

## How it is organised

| Folder | What it is |
| --- | --- |
| `Assets/Hexfront/Core` | The whole game as plain C#: no Unity types, no floating point. Its assembly definition has *No Engine References* ticked, so the compiler enforces that. |
| `Assets/Hexfront/Game` | Unity presentation: board rendering, camera, input, HUD, menus, LAN sockets, saving. |
| `Assets/Hexfront/Editor` | One-time project setup and the iOS permission hook. |
| `Assets/Hexfront/Resources/Shaders` | Two small shaders (flat-shaded vertex colour, transparent overlay). |
| `Assets/Tests/EditMode` | NUnit tests for the rules. Run them from **Window > General > Test Runner**. |

### The one rule that makes multiplayer work
The simulation never looks at frame time, animation or anything device-specific. A round is a pure function:

```
next state = Simulation.Resolve(state, player 0 orders, player 1 orders)
```

The 3D view only *plays back* the list of events that function returns, then re-syncs itself to the state. That is why two phones can stay in step: the host sends both order lists plus a hash of its resulting state, the client runs the same function and compares hashes, and asks for the full state if they ever differ.

Key files in `Core`:
- `Simulation.cs` - the 11-step round resolution and the movement-conflict rules.
- `Orders.cs` - order types and `OrderValidator`, used identically by the UI, the AI and the host.
- `Defs.cs` - every unit, building and terrain stat, plus a `Balance` class of tunable constants.
- `MapGenerator.cs` - seeded generation (point-mirrored for fairness, then validated) and the three handcrafted maps.
- `AiPlayer.cs` - Easy / Normal / Hard opponent.

### Movement conflicts (deterministic)
- A unit never enters a hex holding an enemy of its layer (ground or air); it waits and retries on later ticks.
- Two enemies entering the same empty hex on the same tick: the heavier unit wins (mass, then health). An exact tie blocks both.
- Friendly units may pass through each other but never end stacked: the later arrival backs up along its own path.
- All shots are computed before any damage lands, so a unit destroyed this round still fires this round.

## Game summary
- **Win** by destroying every enemy Headquarters (or by score if a round limit is set).
- **Credits** come from the HQ, Generators, Supply Depots and captured sites (oil, industry, supply caches). Infantry-type units capture a site by ending the round on it.
- **20 units** across infantry, vehicles and aircraft; **8 buildings** with upgrades that unlock stronger units.
- **Fog of war**, terrain cover, high ground, aircraft fuel, repairs and support auras.
- **Maps:** 3 handcrafted, plus generated maps in 6 regions with adjustable size, terrain, water, resources, objectives and start distance. Maps can be saved, favourited and shared as a short code.

## What has and has not been verified
This project was written without access to the Unity Editor, so be clear-eyed about its state:

- **Verified by running it:** the rules core (24 tests, including full AI-vs-AI matches on every map checking determinism, save/load round-trips and board invariants) and the LAN layer (handshake, order delivery, drop detection, automatic reconnect, UDP discovery over real sockets).
- **Verified by compiling only:** the Unity layer type-checks against Unity's API with both input backends, but it has **not been run or seen**. Expect to fix layout, proportions and visual details on first launch.
- **Not verified at all:** the two shader files (nothing here can compile ShaderLab) and behaviour on real iOS/Android devices. If a shader fails, the game falls back to a built-in unlit shader instead of rendering pink.

## Deliberate simplifications
- APCs and Transport Helicopters do not carry other units; they are fast units that can capture sites.
- Lighting is a cheap per-vertex sun with blob shadows rather than real-time shadows. The shaders work in both the Built-in and Universal render pipelines; no pipeline asset is configured.
- The AI knows the map layout and where the enemy started, but only sees units that fog of war lets it see.
- If the host device disconnects the match pauses until it returns (no host migration).
- Two evenly matched AIs can stalemate; set a round limit if you want a guaranteed finish.
