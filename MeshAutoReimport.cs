using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BaseX;
using NeosModLoader;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace MeshAutoReimport
{
    public class MeshAutoReimport : NeosMod
    {
        public override string Name => "MeshAutoReimport";
        public override string Author => "TheJebForge";
        public override string Version => "1.0.0";

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony($"net.{Author}.{Name}");
            harmony.PatchAll();
            PatchRunImport(harmony);
        }

        private static Dictionary<string, IField<bool>> dialogCheckboxes = new Dictionary<string, IField<bool>>();

        static void AddReimportCheckbox(ModelImportDialog modelImportDialog, UIBuilder ui)
        {
            var field = modelImportDialog.Slot.AttachComponent<ValueField<bool>>();

            foreach (var path in modelImportDialog.Paths)
            {
                if (dialogCheckboxes.ContainsKey(path))
                    dialogCheckboxes.Remove(path);
                
                dialogCheckboxes.Add(path, field.Value);
            }

            ui.HorizontalLayout(4f);
            ui.BooleanMemberEditor(field.Value);
            ui.Style.FlexibleWidth = 100f;
            ui.Text("Reimport when file changes");
            ui.Style.FlexibleWidth = -1f;
            ui.NestOut();
        }

        [HarmonyPatch(typeof(ModelImportDialog), "MenuCustom")]
        class ModelImportDialog_MenuCustom_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int index = -1;

                List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

                for (int i = 0; i < codes.Count; i++)
                {
                    CodeInstruction instr = codes[i];

                    if (instr.opcode == OpCodes.Ldstr &&
                        instr.operand.ToString().Contains("Importer.Model.Advanced.AssetsOnObject"))
                    {
                        Msg("Found!");

                        index = i + 6;
                        break;
                    }
                }

                if (index > -1)
                {
                    MethodInfo method = typeof(MeshAutoReimport).GetMethod("AddReimportCheckbox", (BindingFlags)(-1));
                    
                    codes.InsertRange(index, new []
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Call, method)
                    });
                    Msg("Patched");
                }

                return codes.AsEnumerable();
            }
        }

        static void PatchRunImport(Harmony harmony)
        {
            MethodInfo method = AccessTools.TypeByName("FrooxEngine.ModelImportDialog+<>c__DisplayClass70_0+<<RunImport>b__1>d")
                .GetMethod("MoveNext", (BindingFlags) (-1));

            MethodInfo patchMethod =
                typeof(MeshAutoReimport).GetMethod("ModelImportDialog_RunImport_Task_Patch", (BindingFlags)(-1));

            harmony.Patch(method, transpiler: new HarmonyMethod(patchMethod));
        }

        static IEnumerable<CodeInstruction> ModelImportDialog_RunImport_Task_Patch(IEnumerable<CodeInstruction> instructions)
        {
            int index = -1;
            LocalBuilder localVar = null;

            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];

                if (instr.operand != null && instr.opcode == OpCodes.Stloc_S && ((LocalBuilder)instr.operand).LocalIndex == 4)
                {
                    Msg("Found!");

                    index = i + 1;
                    localVar = (LocalBuilder)instr.operand;
                    break;
                }
            }

            if (index > -1)
            {
                codes.InsertRange(index, new []
                {
                    new CodeInstruction(OpCodes.Ldloc_S, localVar),
                    new CodeInstruction(OpCodes.Ldloc_3),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    CodeInstruction.LoadField(AccessTools.TypeByName("FrooxEngine.ModelImportDialog+<>c__DisplayClass70_0"), "settings"),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    CodeInstruction.LoadField(AccessTools.TypeByName("FrooxEngine.ModelImportDialog+<>c__DisplayClass70_0"), "<>4__this"),
                    CodeInstruction.LoadField(AccessTools.TypeByName("FrooxEngine.ModelImportDialog"), "_assetsOnObject"),
                    CodeInstruction.Call(typeof(MeshAutoReimport), "RegisterForAutoReimport")
                });
                
                Msg("Patched");
            }

            return codes.AsEnumerable();
        }

        static void RegisterForAutoReimport(Slot targetSlot, string path, ModelImportSettings settings, Sync<bool> assetsOnObject)
        {
            Msg("Checking if import dialog was registered");
            if (!dialogCheckboxes.ContainsKey(path)) return;
            Msg("Checking if checkbox was checked");
            
            bool checkboxValue = dialogCheckboxes[path].Value;
            dialogCheckboxes.Remove(path);
            
            if (!checkboxValue) return;

            Msg("Registered " + path + " for reimport");
            
            FileInfo info = new FileInfo(path);

            if (info.Directory == null) return;
            
            FileSystemWatcher watcher = new FileSystemWatcher(info.Directory.FullName);

            watcher.Filter = info.Name;
            watcher.NotifyFilter = NotifyFilters.Attributes
                                   | NotifyFilters.CreationTime
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.FileName
                                   | NotifyFilters.LastAccess
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Security
                                   | NotifyFilters.Size;
            watcher.EnableRaisingEvents = true;

            watcher.Changed += (sender, args) =>
            {
                if (args.FullPath == info.FullName)
                {
                    Msg("File reported as changed, reimporting!");
                    
                    targetSlot.RunSynchronously(() =>
                    {
                        if (targetSlot == null)
                        {
                            watcher.Dispose();
                        }
                        else
                        {
                            float3 position = targetSlot.GlobalPosition;
                            targetSlot.DestroyChildren();
                            
                            targetSlot.StartGlobalTask(async () =>
                            {
                                if (targetSlot != null) {
                                    Slot slot1 = targetSlot.World.AddSlot("Import indicator");
                                    slot1.PersistentSelf = false;
                                    NeosLogoMenuProgress logoMenuProgress = slot1.AttachComponent<NeosLogoMenuProgress>();
                                    logoMenuProgress.Spawn(targetSlot.GlobalPosition, 0.05f, true);
                                    logoMenuProgress.UpdateProgress(-1f, "Waiting", "");

                                    await ModelImporter.ImportModelAsync(path, targetSlot, settings,
                                        (bool)assetsOnObject ? targetSlot.AddSlot("Assets") : null, logoMenuProgress);
                                    if (targetSlot != null) targetSlot.GlobalPosition = position;
                                }
                            });
                        }
                    });
                }
            };
        }
    }
}
