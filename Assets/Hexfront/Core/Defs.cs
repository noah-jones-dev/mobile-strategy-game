using System;

namespace Hexfront.Core
{
    // ------------------------------------------------------------------ terrain

    public enum TerrainType : byte { Grass, Forest, Desert, Mountain, Water, Beach, Snow, Rocky, Urban }

    public enum MoveClass : byte { Foot, Wheeled, Tracked, Air }

    public static class TerrainDefs
    {
        public const int Count = 9;
        public const int Impassable = -1;

        //                                    Grass Forest Desert Mount Water Beach Snow Rocky Urban
        static readonly int[] Foot    = { 1, 2, 1, 3, -1, 1, 2, 2, 1 };
        static readonly int[] Wheeled = { 1, 3, 2, -1, -1, 2, 2, 3, 1 };
        static readonly int[] Tracked = { 1, 2, 1, -1, -1, 1, 2, 2, 1 };
        static readonly int[] Defense = { 0, 25, 0, 40, 0, -10, 0, 20, 30 };
        static readonly string[] Names = { "Grassland", "Forest", "Desert", "Mountain", "Water", "Beach", "Snow", "Rocky", "Urban" };

        /// <summary>Movement points to enter a tile, or Impassable.</summary>
        public static int MoveCost(TerrainType t, MoveClass c)
        {
            switch (c)
            {
                case MoveClass.Foot: return Foot[(int)t];
                case MoveClass.Wheeled: return Wheeled[(int)t];
                case MoveClass.Tracked: return Tracked[(int)t];
                default: return 1; // aircraft ignore terrain
            }
        }

        /// <summary>Defense modifier in percent for a ground unit standing on this terrain.</summary>
        public static int DefensePercent(TerrainType t) => Defense[(int)t];

        public static string Name(TerrainType t) => Names[(int)t];

        public static bool IsBuildable(TerrainType t) =>
            t != TerrainType.Water && t != TerrainType.Mountain && t != TerrainType.Forest;
    }

    /// <summary>Neutral strategic locations that pay income to whoever holds them.</summary>
    public enum SiteType : byte { None, OilField, Industry, SupplyCache }

    public static class SiteDefs
    {
        public static int Income(SiteType s)
        {
            switch (s)
            {
                case SiteType.OilField: return 60;
                case SiteType.Industry: return 40;
                case SiteType.SupplyCache: return 25;
                default: return 0;
            }
        }

        public static string Name(SiteType s)
        {
            switch (s)
            {
                case SiteType.OilField: return "Oil Field";
                case SiteType.Industry: return "Industrial Facility";
                case SiteType.SupplyCache: return "Supply Cache";
                default: return "";
            }
        }
    }

    // ------------------------------------------------------------------ units

    public enum UnitCategory : byte { Infantry, Vehicle, Air }

    public enum UnitType : byte
    {
        RifleSquad, HeavyInfantry, AntiTankSquad, RocketInfantry, ReconSquad, Engineers,
        ReconVehicle, Apc, MainBattleTank, HeavyTank, Artillery, RocketArtillery, AntiAir, CommandVehicle, SupplyTruck,
        AttackHelicopter, TransportHelicopter, FighterJet, Bomber, ReconAircraft,
    }

    [Flags]
    public enum UnitFlags
    {
        None = 0,
        /// <summary>Captures a strategic site by ending the round on it.</summary>
        CanCapture = 1,
        /// <summary>Indirect fire: cannot attack in a round in which it moved.</summary>
        Indirect = 2,
        /// <summary>Heals adjacent friendly units each round.</summary>
        Supply = 4,
        /// <summary>Adjacent friendly units deal bonus damage.</summary>
        Command = 8,
        /// <summary>Can seize badly damaged enemy buildings and patches up friendly ones.</summary>
        Engineer = 16,
    }

    public sealed class UnitDef
    {
        public UnitType Type;
        public string Name;
        public string Short;       // 2-3 letter tag shown on the board
        public string Description;
        public UnitCategory Category;
        public MoveClass MoveClass;
        public int Cost, MaxHp, Armor;
        public int AtkInfantry, AtkVehicle, AtkAir, AtkBuilding;
        public int Move, MinRange, MaxRange, Vision;
        /// <summary>Rounds an aircraft can stay away from an Airfield. 0 = no fuel needed.</summary>
        public int Fuel;
        /// <summary>Tie-breaker when two enemy units try to enter the same hex: heavier wins.</summary>
        public int Mass;
        public BuildingType Producer;
        public int ProducerLevel;
        public UnitFlags Flags;

        public bool Has(UnitFlags f) => (Flags & f) != 0;
        public bool IsAir => Category == UnitCategory.Air;
        public bool CanAttackAnything => AtkInfantry + AtkVehicle + AtkAir + AtkBuilding > 0;

        public int AttackVs(UnitCategory c)
        {
            switch (c)
            {
                case UnitCategory.Infantry: return AtkInfantry;
                case UnitCategory.Vehicle: return AtkVehicle;
                default: return AtkAir;
            }
        }
    }

    public static class UnitDefs
    {
        public static readonly UnitDef[] All = Build();
        public static int Count => All.Length;
        public static UnitDef Get(UnitType t) => All[(int)t];

        static UnitDef U(UnitType type, string name, string tag, UnitCategory cat, MoveClass mc,
            int cost, int hp, int armor, int aInf, int aVeh, int aAir, int aBld,
            int move, int minR, int maxR, int vision, BuildingType producer, int level,
            UnitFlags flags, int mass, int fuel, string desc)
        {
            return new UnitDef
            {
                Type = type, Name = name, Short = tag, Category = cat, MoveClass = mc,
                Cost = cost, MaxHp = hp, Armor = armor,
                AtkInfantry = aInf, AtkVehicle = aVeh, AtkAir = aAir, AtkBuilding = aBld,
                Move = move, MinRange = minR, MaxRange = maxR, Vision = vision,
                Producer = producer, ProducerLevel = level, Flags = flags, Mass = mass, Fuel = fuel,
                Description = desc,
            };
        }

        static UnitDef[] Build()
        {
            const UnitCategory I = UnitCategory.Infantry, V = UnitCategory.Vehicle, A = UnitCategory.Air;
            const BuildingType BK = BuildingType.Barracks, VF = BuildingType.VehicleFactory, AF = BuildingType.Airfield;
            const UnitFlags Cap = UnitFlags.CanCapture, None = UnitFlags.None;
            var d = new UnitDef[20];
            //                                                                          cost  hp  arm  inf veh air bld  mv rng   vis
            d[0] = U(UnitType.RifleSquad, "Rifle Squad", "RIF", I, MoveClass.Foot,       100, 100,   0, 35, 10,  5, 10,  3, 1, 1, 3, BK, 1, Cap, 1, 0,
                "Cheap all-round infantry. Captures sites.");
            d[1] = U(UnitType.HeavyInfantry, "Heavy Infantry", "HVY", I, MoveClass.Foot, 160, 130,  15, 50, 18,  8, 15,  2, 1, 1, 2, BK, 2, Cap, 2, 0,
                "Tough, slow infantry that shreds other foot soldiers.");
            d[2] = U(UnitType.AntiTankSquad, "Anti-Tank Squad", "AT", I, MoveClass.Foot, 180,  90,   0, 15, 70,  0, 30,  2, 1, 2, 3, BK, 1, None, 1, 0,
                "Missile team. Counters tanks, weak versus infantry.");
            d[3] = U(UnitType.RocketInfantry, "Rocket Infantry", "RKT", I, MoveClass.Foot, 200, 90,  0, 15, 35, 65, 25,  2, 1, 2, 3, BK, 2, None, 1, 0,
                "Shoulder-launched rockets. Threatens aircraft and light vehicles.");
            d[4] = U(UnitType.ReconSquad, "Recon Squad", "RCN", I, MoveClass.Foot,       120,  70,   0, 20,  5,  0,  5,  4, 1, 1, 6, BK, 1, Cap, 1, 0,
                "Fast scouts with long sight range. Captures sites.");
            d[5] = U(UnitType.Engineers, "Engineers", "ENG", I, MoveClass.Foot,          140,  80,   0, 15,  5,  0, 40,  3, 1, 1, 2, BK, 1, Cap | UnitFlags.Engineer, 1, 0,
                "Demolish buildings, seize damaged enemy buildings, patch up your own.");

            d[6] = U(UnitType.ReconVehicle, "Light Recon Vehicle", "LRV", V, MoveClass.Wheeled, 220, 110, 20, 40, 15, 10, 10, 7, 1, 1, 6, VF, 1, None, 3, 0,
                "Fast wheeled scout. Hunts infantry in open ground.");
            d[7] = U(UnitType.Apc, "Armored Personnel Carrier", "APC", V, MoveClass.Wheeled, 280, 160, 40, 40, 15, 10, 15, 6, 1, 1, 3, VF, 1, Cap, 4, 0,
                "Armoured assault carrier. Fast and able to capture sites.");
            d[8] = U(UnitType.MainBattleTank, "Main Battle Tank", "MBT", V, MoveClass.Tracked, 450, 220, 80, 55, 75, 0, 50, 5, 1, 1, 3, VF, 1, None, 6, 0,
                "Backbone of a ground assault. Strong versus vehicles and infantry.");
            d[9] = U(UnitType.HeavyTank, "Heavy Tank", "HTK", V, MoveClass.Tracked,      700, 320, 120, 65, 95,  0, 65,  4, 1, 1, 2, VF, 2, None, 8, 0,
                "Slow, expensive and very hard to kill.");
            d[10] = U(UnitType.Artillery, "Self-Propelled Artillery", "ART", V, MoveClass.Tracked, 500, 110, 20, 60, 50, 0, 70, 4, 2, 4, 2, VF, 1, UnitFlags.Indirect, 3, 0,
                "Long-range guns. Cannot fire after moving or at adjacent targets.");
            d[11] = U(UnitType.RocketArtillery, "Rocket Artillery", "MLR", V, MoveClass.Wheeled, 650, 100, 10, 80, 60, 0, 60, 4, 3, 6, 2, VF, 2, UnitFlags.Indirect, 3, 0,
                "Extreme range rocket barrage. Helpless up close.");
            d[12] = U(UnitType.AntiAir, "Mobile Anti-Air", "AA", V, MoveClass.Tracked,   380, 140,  35, 30, 15, 90, 10,  5, 1, 3, 4, VF, 1, None, 4, 0,
                "Sweeps aircraft from the sky.");
            d[13] = U(UnitType.CommandVehicle, "Mobile Command Vehicle", "CMD", V, MoveClass.Wheeled, 400, 150, 40, 10, 5, 0, 5, 5, 1, 1, 5, VF, 2, UnitFlags.Command, 4, 0,
                "Adjacent friendly units deal +20% damage.");
            d[14] = U(UnitType.SupplyTruck, "Supply Truck", "SUP", V, MoveClass.Wheeled, 200,  90,   5,  0,  0,  0,  0,  6, 0, 0, 2, VF, 1, UnitFlags.Supply, 2, 0,
                "Repairs adjacent friendly units every round and enables field repairs.");

            d[15] = U(UnitType.AttackHelicopter, "Attack Helicopter", "AH", A, MoveClass.Air, 600, 150, 25, 60, 70, 20, 40, 7, 1, 1, 4, AF, 1, None, 3, 8,
                "Flies over any terrain and tears up ground units.");
            d[16] = U(UnitType.TransportHelicopter, "Transport Helicopter", "TH", A, MoveClass.Air, 350, 130, 15, 15, 5, 0, 5, 8, 1, 1, 4, AF, 1, Cap | UnitFlags.Supply, 2, 10,
                "Air-assault team: captures far-away sites and resupplies units.");
            d[17] = U(UnitType.FighterJet, "Fighter Jet", "FTR", A, MoveClass.Air,       700, 140,  20, 15, 20, 90, 10, 10, 1, 2, 5, AF, 2, None, 3, 5,
                "Air superiority. Destroys helicopters and bombers.");
            d[18] = U(UnitType.Bomber, "Bomber", "BMB", A, MoveClass.Air,                900, 180,  25, 80, 80,  0, 110, 8, 1, 1, 3, AF, 2, None, 4, 5,
                "Devastating against buildings and ground units. Needs escorts.");
            d[19] = U(UnitType.ReconAircraft, "Recon Aircraft", "RCA", A, MoveClass.Air, 400, 100,   5,  0,  0,  0,  0, 12, 0, 0, 8, AF, 1, None, 1, 7,
                "Unarmed. Reveals a huge area of the battlefield.");
            return d;
        }
    }

    // ------------------------------------------------------------------ buildings

    public enum BuildingType : byte
    {
        Headquarters, Barracks, VehicleFactory, Airfield, RadarStation, Turret, SupplyDepot, Generator,
    }

    public sealed class BuildingDef
    {
        public BuildingType Type;
        public string Name;
        public string Short;
        public string Description;
        public int Cost, MaxHp, Armor, BuildRounds, UpgradeCost, MaxLevel, Vision;
        public int Income, IncomePerLevel;
        /// <summary>How far this building projects owned territory (where you may build).</summary>
        public int TerritoryRadius;
        // Only the turret is armed.
        public int AtkInfantry, AtkVehicle, AtkAir, MaxRange;

        public bool IsProducer =>
            Type == BuildingType.Barracks || Type == BuildingType.VehicleFactory || Type == BuildingType.Airfield;
    }

    public static class BuildingDefs
    {
        public static readonly BuildingDef[] All = Build();
        public static int Count => All.Length;
        public static BuildingDef Get(BuildingType t) => All[(int)t];

        static BuildingDef[] Build()
        {
            return new[]
            {
                new BuildingDef { Type = BuildingType.Headquarters, Name = "Headquarters", Short = "HQ", Cost = 1000, MaxHp = 500, Armor = 40,
                    BuildRounds = 3, UpgradeCost = 500, MaxLevel = 3, Vision = 4, Income = 100, IncomePerLevel = 50, TerritoryRadius = 4,
                    Description = "Lose every Headquarters and you lose the match. Upgrades raise income." },
                new BuildingDef { Type = BuildingType.Barracks, Name = "Barracks", Short = "BK", Cost = 250, MaxHp = 300, Armor = 30,
                    BuildRounds = 1, UpgradeCost = 250, MaxLevel = 2, Vision = 3, TerritoryRadius = 3,
                    Description = "Trains infantry. Level 2 unlocks Heavy and Rocket Infantry." },
                new BuildingDef { Type = BuildingType.VehicleFactory, Name = "Vehicle Factory", Short = "VF", Cost = 450, MaxHp = 400, Armor = 40,
                    BuildRounds = 2, UpgradeCost = 400, MaxLevel = 2, Vision = 3, TerritoryRadius = 3,
                    Description = "Builds ground vehicles. Level 2 unlocks advanced armor." },
                new BuildingDef { Type = BuildingType.Airfield, Name = "Airfield", Short = "AF", Cost = 550, MaxHp = 350, Armor = 30,
                    BuildRounds = 2, UpgradeCost = 450, MaxLevel = 2, Vision = 3, TerritoryRadius = 3,
                    Description = "Builds and refuels aircraft. Level 2 unlocks jets and bombers." },
                new BuildingDef { Type = BuildingType.RadarStation, Name = "Radar Station", Short = "RD", Cost = 300, MaxHp = 200, Armor = 20,
                    BuildRounds = 1, UpgradeCost = 250, MaxLevel = 2, Vision = 7, TerritoryRadius = 2,
                    Description = "Reveals a wide area. Level 2 sees even further." },
                new BuildingDef { Type = BuildingType.Turret, Name = "Defensive Turret", Short = "TU", Cost = 250, MaxHp = 250, Armor = 60,
                    BuildRounds = 1, UpgradeCost = 200, MaxLevel = 2, Vision = 4, TerritoryRadius = 1,
                    AtkInfantry = 45, AtkVehicle = 55, AtkAir = 40, MaxRange = 3,
                    Description = "Automatically fires at enemies within 3 hexes." },
                new BuildingDef { Type = BuildingType.SupplyDepot, Name = "Supply Depot", Short = "SD", Cost = 200, MaxHp = 250, Armor = 20,
                    BuildRounds = 1, UpgradeCost = 150, MaxLevel = 2, Vision = 2, Income = 20, IncomePerLevel = 15, TerritoryRadius = 3,
                    Description = "Small income. Repairs adjacent friendly units every round." },
                new BuildingDef { Type = BuildingType.Generator, Name = "Resource Generator", Short = "GN", Cost = 300, MaxHp = 200, Armor = 10,
                    BuildRounds = 2, UpgradeCost = 250, MaxLevel = 2, Vision = 2, Income = 50, IncomePerLevel = 30, TerritoryRadius = 2,
                    Description = "Steady income. A long-term investment worth defending." },
            };
        }
    }

    /// <summary>Tunable rule constants, gathered in one place for balancing.</summary>
    public static class Balance
    {
        public const int DamageVariancePercent = 10;   // each hit rolls +/- this much
        public const int DefendBonusPercent = 40;      // Defend order: damage taken reduced by this
        public const int DefendAttackPercent = 50;     // ...but the unit only returns half fire
        public const int CommandBonusPercent = 20;
        public const int ElevationBonusPercent = 10;   // direct fire from higher ground
        public const int RangePenaltyPercent = 10;     // direct fire, per hex beyond the first
        public const int VehicleCoverPercent = 50;     // vehicles get only this share of terrain cover
        public const int UnitRepairCostPercent = 25;   // of purchase cost
        public const int UnitRepairHealPercent = 50;   // of max hp
        public const int BuildingRepairCostPercent = 20;
        public const int BuildingRepairHealPercent = 35;
        public const int SupplyHealPercent = 15;
        public const int EngineerPatchPercent = 10;
        public const int SeizeThresholdPercent = 50;   // engineers seize buildings at or below this hp
        public const int TurretLevelBonusPercent = 30;
        public const int RadarLevelBonus = 3;
        public const int SiteVision = 2;
        public const int SiteTerritoryRadius = 1;
        public const int HighGroundVisionBonus = 1;
    }
}
