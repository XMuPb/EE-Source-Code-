using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace BandItPlus.Patches
{
    [HarmonyPatch]
    public static class GetMergedXmlDebugPatch
    {
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_xml_debug.log");

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.ObjectSystem.MBObjectManager");
            return type == null ? null : AccessTools.Method(type, "GetMergedXmlForManaged");
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Prefix(string id)
        {
            try
            {
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] GetMergedXmlForManaged id='" + (id ?? "<NULL>") + "'" + Environment.NewLine);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    public static class LoadXmlWithValidationDebugPatch
    {
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_xml_debug.log");

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.ObjectSystem.MBObjectManager");
            return type == null ? null : AccessTools.Method(type, "LoadXmlWithValidation");
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Prefix(string xmlPath, string xsdPath)
        {
            try
            {
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "]   LoadXmlWithValidation xml='" + (xmlPath ?? "<NULL>") + "' xsd='" + (xsdPath ?? "<NULL>") + "'" + Environment.NewLine);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    public static class CreateDocumentFromXmlFileDebugPatch
    {
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_xml_debug.log");

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.ObjectSystem.MBObjectManager");
            return type == null ? null : AccessTools.Method(type, "CreateDocumentFromXmlFile");
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Prefix(string xmlPath, string xsdPath)
        {
            try
            {
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "]     CreateDocumentFromXmlFile xml='" + (xmlPath ?? "<NULL>") + "' xsd='" + (xsdPath ?? "<NULL>") + "'" + Environment.NewLine);
            }
            catch { }
        }
    }
}
