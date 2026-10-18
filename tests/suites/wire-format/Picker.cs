// Tests what the GPU dropdown offers, and which entry it shows as selected.
//
// Two rules are being checked, and the second matters more than it looks.
//
// The list contains only adapters that are actually present. The entry shown as selected is
// the user's pick when that adapter is there, and otherwise whatever is genuinely being read.
// That second case is display only: it must never be written back into settings, or switching
// a card off would silently convert a deliberate choice of the discrete GPU into a permanent
// pin to the integrated one, and the discrete card would never be picked up again.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

static class Picker
{
    static Type _gpuInfo = null!, _choice = null!;
    static MethodInfo _build = null!, _resolve = null!;

    static object Gpu(string id, string name, bool discrete)
    {
        var g = Activator.CreateInstance(_gpuInfo)!;
        _gpuInfo.GetProperty("Id")!.SetValue(g, id);
        _gpuInfo.GetProperty("Name")!.SetValue(g, name);
        _gpuInfo.GetProperty("IsDiscrete")!.SetValue(g, discrete);
        return g;
    }

    static IList RawBuild(object[] gpus)
    {
        var listType = typeof(List<>).MakeGenericType(_gpuInfo);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var g in gpus) list.Add(g);
        return (IList)_build.Invoke(null, new object[] { list })!;
    }

    static List<(string Id, string Label, string Detail)> Build(object[] gpus) =>
        RawBuild(gpus).Cast<object>().Select(Describe).ToList();

    static (string Id, string Label, string Detail) Describe(object c) => (
        (string)_choice.GetProperty("Id")!.GetValue(c)!,
        (string)_choice.GetProperty("Label")!.GetValue(c)!,
        (string)_choice.GetProperty("Detail")!.GetValue(c)!);

    /// Builds the list for these adapters, then asks which entry would be shown as selected.
    static (string Id, string Label, string Detail)? Selected(object[] gpus, string pinned, string activeName)
    {
        var choices = RawBuild(gpus);
        var picked = _resolve.Invoke(null, new object[] { choices, pinned, activeName });
        return picked == null ? null : Describe(picked);
    }

    const string Nv    = "/gpu-nvidia/0";
    const string Intel = "/gpu-intel-integrated/x";
    const string NvName    = "NVIDIA GeForce RTX 3050 6GB Laptop GPU";
    const string IntelName = "Intel(R) UHD Graphics";

    public static void Run(Assembly asm, Action<string, bool, string> check)
    {
        _gpuInfo = asm.GetType("Pulse.Services.GpuInfo")!;
        _choice  = asm.GetType("Pulse.Models.GpuChoice") ?? asm.GetType("Pulse.GpuChoice")!;

        var vm = asm.GetType("Pulse.ViewModels.SettingsViewModel");
        _build   = vm?.GetMethod("BuildGpuChoices", BindingFlags.Public | BindingFlags.Static)!;
        _resolve = vm?.GetMethod("ResolveSelected", BindingFlags.Public | BindingFlags.Static)!;

        if (_choice == null || _build == null || _resolve == null)
        {
            check("the picker logic is reachable", false, "type or method not found");
            return;
        }

        var nvidia = Gpu(Nv, NvName, true);
        var intel  = Gpu(Intel, IntelName, false);

        var both     = new[] { nvidia, intel };
        var intelOnly = new[] { intel };
        var nvOnly    = new[] { nvidia };

        // ── the list ────────────────────────────────────────────────────────────────
        Console.WriteLine("\nThe dropdown lists only adapters that are actually present:");

        var listBoth = Build(both);
        check("both cards plus automatic", listBoth.Count == 3,
              string.Join(" / ", listBoth.Select(c => c.Label)));
        check("automatic leads the list", listBoth[0].Id == "" && listBoth[0].Label == "Automatic",
              listBoth[0].Label);
        check("each card carries its own identifier",
              listBoth.Any(c => c.Id == Nv && c.Detail == "Discrete")
              && listBoth.Any(c => c.Id == Intel && c.Detail == "Integrated"), "");

        var listIntel = Build(intelOnly);
        check("with one card left, only that card is listed", listIntel.Count == 1,
              string.Join(" / ", listIntel.Select(c => c.Label)));
        check("automatic is dropped, there is nothing to decide",
              listIntel.All(c => c.Label != "Automatic"), listIntel[0].Label);
        check("no entry for hardware that is not there",
              listIntel.All(c => c.Id != Nv && c.Detail != "Not available right now"), "");
        check("and never a raw device identifier as a label",
              listIntel.All(c => !c.Label.StartsWith("/gpu")), listIntel[0].Label);

        // ── the reported bug ────────────────────────────────────────────────────────
        Console.WriteLine("\nPinned to the discrete card, then the card is switched off:");

        var fallback = Selected(intelOnly, Nv, IntelName);
        check("something is shown as selected", fallback != null,
              fallback?.Label ?? "nothing");
        check("it is the integrated GPU, by name", fallback?.Label == IntelName,
              fallback?.Label ?? "");
        check("not the identifier of the missing card", fallback?.Id != Nv, fallback?.Id ?? "");
        check("and not marked unavailable", fallback?.Detail == "Integrated", fallback?.Detail ?? "");

        Console.WriteLine("\nThe same, the other way round:");

        var reverse = Selected(nvOnly, Intel, NvName);
        check("pinned to integrated, only the discrete card present", reverse?.Label == NvName,
              reverse?.Label ?? "nothing");
        check("it reads as discrete", reverse?.Detail == "Discrete", reverse?.Detail ?? "");

        // ── the pin must survive ────────────────────────────────────────────────────
        Console.WriteLine("\nShowing a different GPU must not change what was chosen:");

        // The setting is untouched by display, so the pin is still the discrete card when it
        // comes back. Simulated by resolving again with both present and the same stored id.
        var restored = Selected(both, Nv, NvName);
        check("the pinned card is selected again once it returns", restored?.Id == Nv,
              restored?.Label ?? "nothing");
        check("and it is named, not identified", restored?.Label == NvName, restored?.Label ?? "");

        // ── automatic ───────────────────────────────────────────────────────────────
        Console.WriteLine("\nOn automatic, the selection follows whatever is being read:");

        var autoBoth = Selected(both, "", NvName);
        check("with both present it shows Automatic", autoBoth?.Label == "Automatic",
              autoBoth?.Label ?? "nothing");

        var autoIntel = Selected(intelOnly, "", IntelName);
        check("with only integrated left it names that card", autoIntel?.Label == IntelName,
              autoIntel?.Label ?? "nothing");
        check("that entry carries the card's real identifier, so it can be pinned deliberately",
              autoIntel?.Id == Intel, autoIntel?.Id ?? "");

        var autoNv = Selected(nvOnly, "", NvName);
        check("with only the discrete card left it names that one", autoNv?.Label == NvName,
              autoNv?.Label ?? "nothing");

        // ── a deliberate choice that is present ─────────────────────────────────────
        Console.WriteLine("\nAn adapter the user picked, while it is present:");

        check("pinned integrated with both present", Selected(both, Intel, NvName)?.Id == Intel,
              Selected(both, Intel, NvName)?.Label ?? "");
        check("pinned integrated with only integrated present",
              Selected(intelOnly, Intel, IntelName)?.Id == Intel,
              Selected(intelOnly, Intel, IntelName)?.Label ?? "");

        // ── nothing known yet ───────────────────────────────────────────────────────
        Console.WriteLine("\nBefore anything has been enumerated:");

        check("no adapters means no entries", Build(Array.Empty<object>()).Count == 0, "");
        check("and nothing is selected", Selected(Array.Empty<object>(), Nv, "") == null,
              "the saved choice is left alone until hardware is known");
        check("an unknown active name still selects something",
              Selected(intelOnly, "", "Some Other GPU")?.Label == IntelName,
              "falls back to the first entry rather than showing blank");
    }
}
