using System.Reflection;
using System.Runtime.CompilerServices;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static readonly (string Name, string Json)[] ReferenceForms =
    [
        ("array", "[\"切换账号\"]"),
        ("string", "\"切换账号\"")
    ];

    private static int Main()
    {
        var tests = new List<(string Name, Action Run)>();
        foreach (var type in new[] { "select", "switch" })
        foreach (var form in ReferenceForms)
        {
            tests.Add(($"{type}/{form.Name}: new task uses default No and its child defaults", () =>
            {
                var definition = ReadInterface(form.Json, type);
                var option = definition.Task![0].Option![0];
                TaskLoader.SetDefaultOptionValue(definition, option);
                CheckNoBranch(option);
            }));
            tests.Add(($"{type}/{form.Name}: cloned task uses default No independently", () =>
            {
                var definition = ReadInterface(form.Json, type);
                var original = definition.Task![0];
                var clone = original.Clone();
                TaskLoader.SetDefaultOptionValue(definition, clone.Option![0]);
                CheckNoBranch(clone.Option[0]);
                Check(original.Option![0].Index == null, "initializing a clone changed the task definition");
            }));
        }

        foreach (var form in ReferenceForms)
        foreach (var defaultCase in new string?[] { null, "MissingCase" })
        {
            tests.Add(($"{form.Name}: {defaultCase ?? "absent"} default falls back to first case", () =>
            {
                var definition = ReadInterface(form.Json, defaultCase: defaultCase);
                var option = definition.Task![0].Option![0];
                TaskLoader.SetDefaultOptionValue(definition, option);
                Check(option.Index == 0, "missing or invalid default did not select the first case");
                Check(option.SubOptions!.Single().Name == "yesChild", "fallback selected the wrong subtree");
            }));
        }

        foreach (var index in new[] { 0, 1 })
        {
            tests.Add(($"saved index {index} survives defaults, cloning and JSON round-trip", () =>
            {
                var definition = ReadInterface($$"""[{"name":"切换账号","index":{{index}}}]""",
                    defaultCase: index == 0 ? "No" : "Yes");
                var clone = definition.Task![0].Clone();
                var json = JsonConvert.SerializeObject(clone, new MaaInterfaceSelectOptionConverter(false));
                var restored = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceTask>(json,
                    new MaaInterfaceSelectOptionConverter(false))!;
                TaskLoader.SetDefaultOptionValue(definition, restored.Option![0]);
                Check(restored.Option[0].Index == index, "default overwrote an explicit saved choice");
            }));
        }

        tests.Add(("updating an old task preserves Yes and initializes a newly added option to No", () =>
        {
            var definition = ReadInterface("[\"切换账号\",\"新增选项\"]");
            definition.Option!["新增选项"] = definition.Option["切换账号"];
            var savedTask = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceTask>(
                """{"option":[{"name":"切换账号","index":0}]}""",
                new MaaInterfaceSelectOptionConverter(false))!;
            var savedOption = savedTask.Option![0];

            // Bypass UI subscriptions and startup services; exercise the real option merge.
            var item = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
            typeof(DragItemViewModel).GetField("_interfaceItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(item, savedTask);
            typeof(TaskLoader).GetMethod("UpdateOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(new TaskLoader(definition, null!), [item, definition.Task![0]]);

            Check(ReferenceEquals(savedTask.Option[0], savedOption) && savedOption.Index == 0,
                "updating options replaced the saved Yes choice");
            Check(savedTask.Option.Count == 2, "new option was not added");
            CheckNoBranch(savedTask.Option[1]);
        }));

        tests.Add(("checkbox defaults and an explicitly empty selection are preserved", () =>
        {
            var definition = ReadInterface(ReferenceForms[0].Json, "checkbox");
            var option = definition.Task![0].Option![0];
            TaskLoader.SetDefaultOptionValue(definition, option);
            Check(option.SelectedCases!.SequenceEqual(["No"]), "checkbox default was lost");
            option.SelectedCases = [];
            TaskLoader.SetDefaultOptionValue(definition, option);
            Check(option.SelectedCases.Count == 0, "empty checkbox selection was overwritten");
        }));

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetBaseException().Message}");
            }
        }
        Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static MaaInterface ReadInterface(string optionsJson, string type = "switch", string? defaultCase = "No")
    {
        var json = JObject.Parse("""
            {
              "task": [{"name":"测试任务","entry":"Entry"}],
              "option": {
                "切换账号": {
                  "cases": [
                    {"name":"Yes","option":["yesChild"]},
                    {"name":"No","option":["noChild"]}
                  ]
                },
                "yesChild": {"cases":[{"name":"First"},{"name":"Second"}],"default_case":"Second"},
                "noChild": {"cases":[{"name":"First"},{"name":"Second"}],"default_case":"Second"}
              }
            }
            """);
        json["task"]![0]!["option"] = JToken.Parse(optionsJson);
        json["option"]!["切换账号"]!["type"] = type;
        if (defaultCase != null)
            json["option"]!["切换账号"]!["default_case"] = defaultCase;
        return JsonConvert.DeserializeObject<MaaInterface>(json.ToString(),
            new MaaInterfaceSelectOptionConverter(false))!;
    }

    private static void CheckNoBranch(MaaInterface.MaaInterfaceSelectOption option)
    {
        Check(option.Index == 1, $"expected No (index 1), got {option.Index?.ToString() ?? "null"}");
        var child = option.SubOptions!.Single();
        Check(child.Name == "noChild" && child.Index == 1, "default No subtree was not initialized");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
