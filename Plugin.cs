using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SmoothRegen
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("valheim.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "morgott.valheim.smoothregen";
        public const string PluginName = "SmoothRegen";
        public const string PluginVersion = "1.0.0";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Window;
        internal static ConfigEntry<float> InstantFraction;

        private Harmony _harmony;

        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true,
                "Spread the food health tick out over time instead of applying it in one jump.");

            Window = Config.Bind("General", "SmoothingWindow", 10f,
                new ConfigDescription(
                    "Seconds to spread each health tick over. Vanilla ticks every 10 seconds, so 10 " +
                    "(the maximum, and the default) means healing arrives continuously; lower values " +
                    "pay each tick out faster and then wait. Total healing per minute is unchanged " +
                    "at any value.",
                    new AcceptableValueRange<float>(0.5f, RegenBuffer.VanillaTickPeriod)));

            InstantFraction = Config.Bind("General", "InstantFraction", 0f,
                new ConfigDescription(
                    "Share of each health tick applied immediately, at the moment the tick fires. " +
                    "The remaining share is spread over SmoothingWindow. Smoothing always arrives " +
                    "later than vanilla's instant jump - on average by half the window - and this " +
                    "is the knob for that trade-off. 0.0 = fully smooth, smoothest bar, most lag. " +
                    "1.0 = vanilla behaviour, no smoothing, no lag. Total healing is unchanged at " +
                    "any value.",
                    new AcceptableValueRange<float>(0f, 1f)));

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            State.Buffer.Clear();
        }
    }
}
