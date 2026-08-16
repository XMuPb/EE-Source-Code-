using TaleWorlds.Localization;

namespace BandItPlus
{
    public static class Localization
    {
        public static string Get(string key, string fallback)
        {
            var text = new TextObject("{=" + key + "}" + fallback);
            return text.ToString();
        }
    }
}
