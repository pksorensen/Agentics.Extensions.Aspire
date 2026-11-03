using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundry.Emulator.Tests;

[Collection(nameof(EmulatorCollection))]
public sealed class EmulatorTests(EmulatorFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    private static HttpRequestMessage Post(string url, object body, string? apiKey = EmulatorFixture.AccountKey, string? header = "api-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (apiKey is not null && header is not null) request.Headers.TryAddWithoutValidation(header, apiKey);
        return request;
    }

    private static List<(string Event, JsonNode Data)> ParseSse(string text) =>
        text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n'))
            .Where(lines => lines.Any(l => l.StartsWith("event: ")))
            .Select(lines => (lines.First(l => l.StartsWith("event: "))[7..],
                              JsonNode.Parse(lines.First(l => l.StartsWith("data: "))[6..])!))
            .ToList();

    [Fact]
    public async Task Streaming_responses_ends_with_completed_and_usage()
    {
        var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", input = "hello there", stream = true }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType?.ToString());
        var events = ParseSse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("response.created", events[0].Event);
        Assert.Equal("response.completed", events[^1].Event);
        Assert.Contains(events, e => e.Event == "response.output_text.delta");
        var seq = events.Select(e => e.Data["sequence_number"]!.GetValue<int>()).ToList();
        Assert.Equal(Enumerable.Range(0, seq.Count), seq);
        var usage = events[^1].Data["response"]!["usage"]!;
        Assert.True(usage["input_tokens"]!.GetValue<long>() > 0);
        Assert.True(usage["output_tokens"]!.GetValue<long>() > 0);
        Assert.Equal(usage["input_tokens"]!.GetValue<long>() + usage["output_tokens"]!.GetValue<long>(), usage["total_tokens"]!.GetValue<long>());
    }

    [Fact]
    public async Task Codex_shaped_loop_advances_scenario_steps()
    {
        var tools = new object[] { new { type = "function", name = "exec_command", parameters = new { type = "object" } } };
        var first = await _client.SendAsync(Post("/line-a/openai/v1/responses", new
        {
            model = "gpt-test-eu", stream = false, tools, prompt_cache_key = "loop-test",
            input = new object[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "byg en kalender" } } } },
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = JsonNode.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var output = body["output"]!.AsArray();
        var call = output.First(o => o!["type"]!.GetValue<string>() == "function_call")!;
        Assert.Equal("exec_command", call["name"]!.GetValue<string>());
        Assert.Contains("mkdir -p en-kalender", call["arguments"]!.GetValue<string>());

        var second = await _client.SendAsync(Post("/line-a/openai/v1/responses", new
        {
            model = "gpt-test-eu", stream = false, tools, prompt_cache_key = "loop-test",
            input = new object[]
            {
                new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "byg en kalender" } } },
                JsonNode.Parse(output.First(o => o!["type"]!.GetValue<string>() == "message")!.ToJsonString())!,
                JsonNode.Parse(call.ToJsonString())!,
                new { type = "function_call_output", call_id = call["call_id"]!.GetValue<string>(), output = "ok" },
            },
        }), TestContext.Current.CancellationToken);
        var text = (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Færdig med en-kalender", JsonNode.Parse(text)!["output"]!.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private static object EnvironmentContext(string shell, string cwd) => new
    {
        type = "message", role = "user",
        content = new[] { new { type = "input_text", text = $"<environment_context>\n  <cwd>{cwd}</cwd>\n  <shell>{shell}</shell>\n</environment_context>" } },
    };

    private async Task<JsonArray> Turn(object[] input, string? session = null)
    {
        var tools = new object[] { new { type = "function", name = "shell_command", parameters = new { type = "object" } } };
        var response = await _client.SendAsync(Post("/line-a/openai/v1/responses", new
        {
            model = "gpt-test-eu", stream = false, tools, prompt_cache_key = session ?? "skriv-" + input.Length, input,
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["output"]!.AsArray();
    }

    private static object UserText(string text) => new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } };

    [Fact]
    public async Task Powershell_agent_gets_files_written_in_powershell_and_a_file_link()
    {
        var context = EnvironmentContext("powershell", @"C:\Users\demo\my app");
        var first = await Turn([context, UserText("skriv side")]);
        var write = first.First(o => o!["type"]!.GetValue<string>() == "function_call")!;
        var command = JsonNode.Parse(write["arguments"]!.GetValue<string>())!["command"]!.GetValue<string>();
        Assert.Contains("New-Item -ItemType Directory -Force -Path 'side'", command);
        Assert.Contains("[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'side/index.html'), @'\n<h1>Hej, 'verden' & co</h1>\n'@)", command);
        Assert.DoesNotContain("<<", command);

        object Done(JsonNode call) => new { type = "function_call_output", call_id = call["call_id"]!.GetValue<string>(), output = "ok" };
        object Copy(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;
        var second = await Turn([context, UserText("skriv side"), .. first.Select(o => Copy(o!)), Done(write)]);
        var list = second.First(o => o!["type"]!.GetValue<string>() == "function_call")!;
        Assert.Equal("Get-ChildItem side", JsonNode.Parse(list["arguments"]!.GetValue<string>())!["command"]!.GetValue<string>());

        var third = await Turn([context, UserText("skriv side"), .. first.Select(o => Copy(o!)), Done(write), .. second.Select(o => Copy(o!)), Done(list)]);
        Assert.Contains("file:///C:/Users/demo/my%20app/side/index.html", third.ToJsonString());
    }

    [Fact]
    public async Task Gate_waits_for_a_yes_typed_or_clicked()
    {
        static object Copy(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;
        static JsonNode CallIn(JsonArray output) => output.First(o => o!["type"]!.GetValue<string>() == "function_call")!;
        static object Output(JsonNode call, string output) => new { type = "function_call_output", call_id = call["call_id"]!.GetValue<string>(), output };

        // Outside Plan mode Codex answers the question tool with an error, so the gate waits for a typed yes.
        var ask = await Turn([UserText("spørg først")], "gate-typed");
        Assert.Equal("request_user_input", CallIn(ask)["name"]!.GetValue<string>());
        object[] history = [UserText("spørg først"), .. ask.Select(o => Copy(o!)), Output(CallIn(ask), "request_user_input is unavailable in Default mode")];
        var wait = await Turn(history, "gate-typed");
        Assert.DoesNotContain(wait, o => o!["type"]!.GetValue<string>() == "function_call");
        Assert.Contains("Skriv ja", wait.ToJsonString());

        var commit = await Turn([.. history, .. wait.Select(o => Copy(o!)), UserText("ja")], "gate-typed");
        Assert.Contains("git commit", CallIn(commit)["arguments"]!.GetValue<string>());

        // A click comes back as the tool's answer and opens the gate at once.
        var clickAsk = await Turn([UserText("spørg først")], "gate-clicked");
        var clicked = await Turn([UserText("spørg først"), .. clickAsk.Select(o => Copy(o!)),
            Output(CallIn(clickAsk), "{\"answers\":{\"commit\":{\"answers\":[\"Ja, commit og deploy (Recommended)\"]}}}")], "gate-clicked");
        Assert.Contains("git commit", CallIn(clicked)["arguments"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ask_project_offers_the_callers_projects_and_wires_the_chosen_remote()
    {
        async Task<string> Mint(string label, string remote) => (await (await _client.PostAsJsonAsync("/_emulator/keys",
            new { customer = "sol-" + label.Length, userPrincipal = "vibe@example.test", context = new { label, gitRemote = remote } },
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["primaryKey"]!.GetValue<string>();
        var key = await Mint("Cockpit / Web", "http://git/cockpit.git");
        await Mint("Cockpit 2 / Web", "http://git/cockpit-2.git");

        async Task<JsonArray> Send(object[] input)
        {
            var tools = new object[] { new { type = "function", name = "shell_command", parameters = new { type = "object" } }, new { type = "function", name = "request_user_input", parameters = new { type = "object" } } };
            // request_user_input only works in Plan mode, which Codex announces in a developer message.
            object planMode = new { type = "message", role = "developer", content = new[] { new { type = "input_text", text = "# Plan Mode (Conversational)" } } };
            input = [planMode, .. input];
            var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", stream = false, tools, prompt_cache_key = "projekt", input }, key, "Ocp-Apim-Subscription-Key"),
                TestContext.Current.CancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["output"]!.AsArray();
        }

        var ask = await Send([UserText("nyt projekt")]);
        var call = ask.First(o => o!["type"]!.GetValue<string>() == "function_call")!;
        var options = JsonNode.Parse(call["arguments"]!.GetValue<string>())!["questions"]![0]!["options"]!.AsArray().Select(o => o!["label"]!.GetValue<string>()).ToList();
        Assert.Equal(["Cockpit / Web", "Cockpit 2 / Web"], options);

        var wired = await Send([UserText("nyt projekt"), .. ask.Select(o => JsonNode.Parse(o!.ToJsonString())!),
            new { type = "function_call_output", call_id = call["call_id"]!.GetValue<string>(), output = "{\"answers\":{\"project\":{\"answers\":[\"Cockpit 2 / Web\"]}}}" }]);
        Assert.Contains("git remote add origin http://git/cockpit-2.git", wired.ToJsonString());
    }

    [Fact]
    public async Task Ask_project_outside_plan_mode_lists_the_projects_and_takes_a_number()
    {
        async Task<string> Mint(string label, string remote) => (await (await _client.PostAsJsonAsync("/_emulator/keys",
            new { customer = "sol-text-" + label.Length, userPrincipal = "typed@example.test", context = new { label, gitRemote = remote } },
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["primaryKey"]!.GetValue<string>();
        var key = await Mint("Tavle / Web", "http://git/tavle.git");
        await Mint("Tavle 2 / Web", "http://git/tavle-2.git");

        async Task<JsonArray> Send(object[] input)
        {
            // Codex offers request_user_input in Default mode too, and refuses to run it there.
            var tools = new object[] { new { type = "function", name = "shell_command", parameters = new { type = "object" } }, new { type = "function", name = "request_user_input", parameters = new { type = "object" } } };
            var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", stream = false, tools, prompt_cache_key = "projekt-tekst", input }, key, "Ocp-Apim-Subscription-Key"),
                TestContext.Current.CancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["output"]!.AsArray();
        }

        var ask = await Send([UserText("nyt projekt")]);
        Assert.DoesNotContain(ask, o => o!["type"]!.GetValue<string>() == "function_call");
        var text = ask.ToJsonString();
        Assert.Contains("1. **Tavle / Web**", text);
        Assert.Contains("2. **Tavle 2 / Web**", text);

        var wired = await Send([UserText("nyt projekt"), .. ask.Select(o => JsonNode.Parse(o!.ToJsonString())!), UserText("2")]);
        Assert.Contains("git remote add origin http://git/tavle-2.git", wired.ToJsonString());
    }

    [Fact]
    public async Task Ask_project_through_homes_mcp_app_searches_calls_the_picker_and_takes_the_pick()
    {
        var minted = await (await _client.PostAsJsonAsync("/_emulator/keys",
            new { customer = "sol-picker", userPrincipal = "picker@example.test", context = new { label = "Tavle 3 / Web", gitRemote = "http://git/tavle-3.git" } },
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken);
        var key = minted!["primaryKey"]!.GetValue<string>();

        async Task<JsonArray> Send(object[] input)
        {
            var tools = new object[]
            {
                new { type = "function", name = "shell_command", parameters = new { type = "object" } },
                new { type = "tool_search", execution = "client", description = "# Tool discovery\n\nYou have access to tools from the following sources:\n- home\n", parameters = new { type = "object" } },
            };
            var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", stream = false, tools, prompt_cache_key = "projekt-app", input }, key, "Ocp-Apim-Subscription-Key"),
                TestContext.Current.CancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["output"]!.AsArray();
        }
        static IEnumerable<JsonNode> Echo(JsonArray output) => output.Select(o => JsonNode.Parse(o!.ToJsonString())!);

        var search = await Send([UserText("nyt projekt")]);
        var searchCall = search.Single(o => o!["type"]!.GetValue<string>() == "tool_search_call")!;
        var searchId = searchCall["call_id"]!.GetValue<string>();
        Assert.StartsWith("call_sim_s0_find_", searchId);

        var found = JsonNode.Parse($$"""
            {"type":"tool_search_output","call_id":"{{searchId}}","execution":"client","status":"completed",
             "tools":[{"type":"namespace","name":"mcp__home","tools":[{"type":"function","name":"choose_project"}]}]}
            """)!;
        var call = await Send([UserText("nyt projekt"), .. Echo(search), found]);
        var picker = call.Single()!;
        Assert.Equal("function_call", picker["type"]!.GetValue<string>());
        Assert.Equal("mcp__home", picker["namespace"]!.GetValue<string>());
        Assert.Equal("choose_project", picker["name"]!.GetValue<string>());

        var output = JsonNode.Parse($$"""{"type":"function_call_output","call_id":"{{picker["call_id"]!.GetValue<string>()}}","output":"{\"projects\":[]}"}""")!;
        var wait = await Send([UserText("nyt projekt"), .. Echo(search), found, .. Echo(call), output]);
        var question = wait.Single()!["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith("Vælg projektet i vinduet herover", question);
        Assert.Contains("1. **Tavle 3 / Web**", question);

        var wired = await Send([UserText("nyt projekt"), .. Echo(search), found, .. Echo(call), output, .. Echo(wait), UserText("Tavle 3 / Web")]);
        Assert.Contains("git remote add origin http://git/tavle-3.git", wired.ToJsonString());

        async Task<JsonNode> Mcp(object message)
        {
            var response = await _client.SendAsync(Post("/mcp", message, key, "api-key"), TestContext.Current.CancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["result"]!;
        }
        var listed = await Mcp(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal("ui://home/choose-project.html", listed["tools"]![0]!["_meta"]!["ui"]!["resourceUri"]!.GetValue<string>());
        var result = await Mcp(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "choose_project", arguments = new { } } });
        Assert.Equal("Tavle 3 / Web", result["structuredContent"]!["projects"]![0]!["label"]!.GetValue<string>());
        var page = await Mcp(new { jsonrpc = "2.0", id = 3, method = "resources/read", @params = new { uri = "ui://home/choose-project.html" } });
        Assert.Equal("text/html;profile=mcp-app", page["contents"]![0]!["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ask_project_calls_homes_picker_directly_when_codex_lists_it_in_a_namespace()
    {
        var minted = await (await _client.PostAsJsonAsync("/_emulator/keys",
            new { customer = "sol-picker-ns", userPrincipal = "picker-ns@example.test", context = new { label = "Tavle 4 / Web", gitRemote = "http://git/tavle-4.git" } },
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken);
        var key = minted!["primaryKey"]!.GetValue<string>();

        async Task<JsonArray> Send(object[] input)
        {
            var tools = new object[]
            {
                new { type = "function", name = "shell_command", parameters = new { type = "object" } },
                new { type = "function", name = "request_user_input", parameters = new { type = "object" } },
                new { type = "namespace", name = "mcp__home", tools = new[] { new { type = "function", name = "choose_project" } } },
            };
            var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", stream = false, tools, prompt_cache_key = "projekt-ns", input }, key, "Ocp-Apim-Subscription-Key"),
                TestContext.Current.CancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["output"]!.AsArray();
        }
        static IEnumerable<JsonNode> Echo(JsonArray output) => output.Select(o => JsonNode.Parse(o!.ToJsonString())!);

        var call = await Send([UserText("nyt projekt")]);
        var picker = call.Single(o => o!["type"]!.GetValue<string>() == "function_call")!;
        Assert.Equal("mcp__home", picker["namespace"]!.GetValue<string>());
        Assert.Equal("choose_project", picker["name"]!.GetValue<string>());

        var output = JsonNode.Parse($$"""{"type":"function_call_output","call_id":"{{picker["call_id"]!.GetValue<string>()}}","output":"{}"}""")!;
        var wait = await Send([UserText("nyt projekt"), .. Echo(call), output]);
        Assert.Contains("1. **Tavle 4 / Web**", wait.Single()!["content"]![0]!["text"]!.GetValue<string>());

        var wired = await Send([UserText("nyt projekt"), .. Echo(call), output, .. Echo(wait), UserText("1")]);
        Assert.Contains("git remote add origin http://git/tavle-4.git", wired.ToJsonString());
    }

    [Fact]
    public async Task A_later_message_starts_the_scenario_when_the_first_one_matched_none()
    {
        var minted = await (await _client.PostAsJsonAsync("/_emulator/keys",
            new { customer = "sol-picker-typo", userPrincipal = "picker-typo@example.test", context = new { label = "Tavle 5 / Web", gitRemote = "http://git/tavle-5.git" } },
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken);
        var key = minted!["primaryKey"]!.GetValue<string>();

        async Task<JsonArray> Send(object[] input)
        {
            var tools = new object[]
            {
                new { type = "function", name = "shell_command", parameters = new { type = "object" } },
                new { type = "namespace", name = "mcp__home", tools = new[] { new { type = "function", name = "choose_project" } } },
            };
            var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", stream = false, tools, prompt_cache_key = "projekt-typo", input }, key, "Ocp-Apim-Subscription-Key"),
                TestContext.Current.CancellationToken);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["output"]!.AsArray();
        }
        static IEnumerable<JsonNode> Echo(JsonArray output) => output.Select(o => JsonNode.Parse(o!.ToJsonString())!);

        var missed = await Send([UserText("nyt prjekt")]);
        Assert.Contains("No scenario matched", missed.ToJsonString());

        var call = await Send([UserText("nyt prjekt"), .. Echo(missed), UserText("nyt projekt")]);
        var picker = call.Single(o => o!["type"]!.GetValue<string>() == "function_call")!;
        Assert.Equal("choose_project", picker["name"]!.GetValue<string>());

        var output = JsonNode.Parse($$"""{"type":"function_call_output","call_id":"{{picker["call_id"]!.GetValue<string>()}}","output":"{}"}""")!;
        var wait = await Send([UserText("nyt prjekt"), .. Echo(missed), UserText("nyt projekt"), .. Echo(call), output]);
        var wired = await Send([UserText("nyt prjekt"), .. Echo(missed), UserText("nyt projekt"), .. Echo(call), output, .. Echo(wait), UserText("1")]);
        Assert.Contains("git remote add origin http://git/tavle-5.git", wired.ToJsonString());
    }

    [Fact]
    public async Task Bash_agent_keeps_the_heredoc_and_the_bash_command()
    {
        var context = EnvironmentContext("bash", "/home/demo/app");
        var first = await Turn([context, UserText("skriv side")]);
        var command = JsonNode.Parse(first.First(o => o!["type"]!.GetValue<string>() == "function_call")!["arguments"]!.GetValue<string>())!["command"]!.GetValue<string>();
        Assert.StartsWith("mkdir -p 'side' && cat > 'side/index.html' <<'FOUNDRY_EMULATOR_EOF'", command);
    }

    [Fact]
    public async Task Dated_chat_requires_api_version()
    {
        var missing = await _client.SendAsync(Post("/openai/deployments/gpt-test/chat/completions",
            new { messages = new[] { new { role = "user", content = "hi" } } }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var ok = await _client.SendAsync(Post("/openai/deployments/gpt-test/chat/completions?api-version=2024-10-21",
            new { messages = new[] { new { role = "user", content = "hi" } } }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = JsonNode.Parse(await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("chat.completion", body["object"]!.GetValue<string>());
        Assert.True(body["usage"]!["total_tokens"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Missing_credentials_is_401_and_unknown_deployment_is_404()
    {
        var anonymous = await _client.SendAsync(Post("/openai/v1/chat/completions",
            new { model = "gpt-test", messages = new[] { new { role = "user", content = "hi" } } }, apiKey: null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var unknown = await _client.SendAsync(Post("/openai/v1/chat/completions",
            new { model = "nope", messages = new[] { new { role = "user", content = "hi" } } }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Gateway_key_attributes_usage_to_customer()
    {
        var response = await _client.SendAsync(Post("/line-a/openai/v1/chat/completions",
            new { model = "gpt-test-eu", messages = new[] { new { role = "user", content = "count me" } } },
            EmulatorFixture.GatewayKey, "Ocp-Apim-Subscription-Key"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var usage = await _client.GetFromJsonAsync<JsonNode>("/_emulator/usage?groupBy=customer,subscription,model&customer=acme",
            TestContext.Current.CancellationToken);
        var row = usage!["rows"]!.AsArray().Single()!;
        Assert.Equal("acme", row["customerId"]!.GetValue<string>());
        Assert.Equal("acme-user", row["subscriptionId"]!.GetValue<string>());
        Assert.Equal("gpt-5-codex", row["model"]!.GetValue<string>());
        Assert.True(row["promptTokens"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Minted_key_attributes_usage_to_a_new_customer()
    {
        var minted = await (await _client.PostAsJsonAsync("/_emulator/keys", new { customer = "brand-new", userPrincipal = "x@example.test" },
            TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken);
        var key = minted!["primaryKey"]!.GetValue<string>();
        var response = await _client.SendAsync(Post("/openai/v1/responses", new { model = "gpt-test", input = "hi" }, key, "Ocp-Apim-Subscription-Key"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var usage = await _client.GetFromJsonAsync<JsonNode>("/_emulator/usage?groupBy=day,customer&customer=brand-new", TestContext.Current.CancellationToken);
        Assert.Equal(1, usage!["rows"]![0]!["requests"]!.GetValue<int>());
    }

    [Fact]
    public async Task Seeded_usage_is_queryable()
    {
        var usage = await _client.GetFromJsonAsync<JsonNode>("/_emulator/usage?groupBy=day&customer=seeded-co", TestContext.Current.CancellationToken);
        Assert.InRange(usage!["rows"]!.AsArray().Count, 6, 8);
    }

    [Fact]
    public async Task Arm_lists_deployments_with_responses_capability()
    {
        var list = await _client.GetFromJsonAsync<JsonNode>(
            "/arm/subscriptions/00000000-0000-4000-8000-0000000000f1/resourceGroups/rg-test/providers/Microsoft.CognitiveServices/accounts/aif-test/deployments?api-version=2025-06-01",
            TestContext.Current.CancellationToken);
        var deployment = list!["value"]!.AsArray().Single()!;
        Assert.Equal("gpt-test", deployment["name"]!.GetValue<string>());
        Assert.Equal("true", deployment["properties"]!["capabilities"]!["responses"]!.GetValue<string>());
        Assert.Equal("gpt-5-codex", deployment["properties"]!["model"]!["name"]!.GetValue<string>());

        var accounts = await _client.GetFromJsonAsync<JsonNode>(
            "/arm/subscriptions/00000000-0000-4000-8000-0000000000f1/providers/Microsoft.CognitiveServices/accounts?api-version=2024-10-01",
            TestContext.Current.CancellationToken);
        Assert.Equal("aif-test", accounts!["value"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Arm_metrics_report_seeded_input_tokens()
    {
        var now = DateTime.UtcNow.Date;
        var url = "/arm/subscriptions/00000000-0000-4000-8000-0000000000f1/resourceGroups/rg-test/providers/Microsoft.CognitiveServices/accounts/aif-test/providers/Microsoft.Insights/metrics"
                  + $"?api-version=2024-02-01&metricnames=InputTokens&interval=P1D&timespan={now.AddDays(-10):yyyy-MM-ddTHH:mm:ssZ}/{now.AddDays(1):yyyy-MM-ddTHH:mm:ssZ}"
                  + "&$filter=" + Uri.EscapeDataString("ModelDeploymentName eq '*'");
        var metrics = await _client.GetFromJsonAsync<JsonNode>(url, TestContext.Current.CancellationToken);
        var series = metrics!["value"]![0]!["timeseries"]![0]!;
        Assert.Equal("modeldeploymentname", series["metadatavalues"]![0]!["name"]!["value"]!.GetValue<string>());
        Assert.True(series["data"]!.AsArray().Sum(d => d!["total"]!.GetValue<double>()) > 0);
    }

    [Fact]
    public async Task Yaml_scenario_loaded_from_directory()
    {
        var list = await _client.GetFromJsonAsync<JsonNode>("/_emulator/scenarios", TestContext.Current.CancellationToken);
        Assert.Empty(list!["errors"]!.AsArray());
        Assert.Contains(list["scenarios"]!.AsArray(), s => s!["name"]!.GetValue<string>() == "byg");
    }

    [Fact]
    public async Task Sim_scenario_key_selects_scenario()
    {
        var response = await _client.SendAsync(Post("/openai/v1/chat/completions",
            new { model = "gpt-test", messages = new[] { new { role = "user", content = "anything" } } }, "sim-scenario:rate-limit-then-succeed"),
            TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.True(response.Headers.Contains("retry-after"));
    }
}
