namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Acceptance for the libghostty mapping-table slice (docs/decisions/csharp-mapping-tables-libghostty.md):
/// `bind-c` with a spec over the real `vt.h` for every function snap's hand-written `Native.cs` imports,
/// the proposals applied through a session, C# emitted with the table and run against the real
/// `libghostty-vt.so`. The library comes from NYRWVY_LIBGHOSTTY_VT (a directory with include/ and lib/),
/// else from a `snap/vendor/libghostty-vt` beside an ancestor of the test; skips without it or clang.
module CSharpMappingTableGhosttyTests =
    /// Every libghostty function snap/app/Native.cs imports.
    let private nativeCsFunctions =
        [ "ghostty_terminal_new"; "ghostty_terminal_set"; "ghostty_terminal_resize"; "ghostty_terminal_vt_write"; "ghostty_terminal_get"
          "ghostty_terminal_scroll_viewport"; "ghostty_terminal_free"
          "ghostty_render_state_new"; "ghostty_render_state_update"; "ghostty_render_state_get"; "ghostty_render_state_free"
          "ghostty_render_state_row_iterator_new"; "ghostty_render_state_row_iterator_next"; "ghostty_render_state_row_get"; "ghostty_render_state_row_iterator_free"
          "ghostty_render_state_row_cells_new"; "ghostty_render_state_row_cells_next"; "ghostty_render_state_row_cells_get"; "ghostty_render_state_row_cells_free"
          "ghostty_cell_get"; "ghostty_row_get"; "ghostty_paste_is_safe"; "ghostty_paste_encode"
          "ghostty_key_encoder_new"; "ghostty_key_encoder_setopt_from_terminal"; "ghostty_key_encoder_encode"; "ghostty_key_encoder_free"
          "ghostty_key_event_new"; "ghostty_key_event_set_action"; "ghostty_key_event_set_key"; "ghostty_key_event_set_mods"
          "ghostty_key_event_set_utf8"; "ghostty_key_event_set_unshifted_codepoint"; "ghostty_key_event_free"
          "ghostty_mouse_encoder_new"; "ghostty_mouse_encoder_setopt"; "ghostty_mouse_encoder_setopt_from_terminal"; "ghostty_mouse_encoder_encode"; "ghostty_mouse_encoder_free"
          "ghostty_mouse_event_new"; "ghostty_mouse_event_set_action"; "ghostty_mouse_event_set_button"; "ghostty_mouse_event_clear_button"
          "ghostty_mouse_event_set_mods"; "ghostty_mouse_event_set_position"; "ghostty_mouse_event_free" ]

    /// What the header cannot say: getters and setters per data kind (`void *` values), structs by value,
    /// bit-pattern references, handles passed by address to be populated, and text the library keeps
    /// pointing to after the call (`set_utf8`: the event does not copy it).
    let private spec = """{ "capabilities": {
  "Ghostty": { "operations": {
    "cellCodepoint": { "symbol": "ghostty_cell_get", "arguments": [{ "from": "cell", "c": "bits64" }, { "value": "GHOSTTY_CELL_DATA_CODEPOINT" }, { "out": "uint32" }] },
    "cellWide": { "symbol": "ghostty_cell_get", "arguments": [{ "from": "cell", "c": "bits64" }, { "value": "GHOSTTY_CELL_DATA_WIDE" }, { "out": "int32" }] },
    "rowWrap": { "symbol": "ghostty_row_get", "arguments": [{ "from": "row", "c": "bits64" }, { "value": "GHOSTTY_ROW_DATA_WRAP" }, { "out": "bool" }] } } },
  "Terminal": { "operations": {
    "mouseTracking": { "symbol": "ghostty_terminal_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING" }, { "out": "bool" }] },
    "viewportActive": { "symbol": "ghostty_terminal_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_TERMINAL_DATA_VIEWPORT_ACTIVE" }, { "out": "bool" }] },
    "setColorForeground": { "symbol": "ghostty_terminal_set", "arguments": [{ "self": true }, { "value": "GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND" },
      { "struct": [{ "from": "red", "c": "uint8" }, { "from": "green", "c": "uint8" }, { "from": "blue", "c": "uint8" }], "by": "pointer" }] },
    "scrollDelta": { "symbol": "ghostty_terminal_scroll_viewport", "arguments": [{ "self": true },
      { "struct": [{ "value": "GHOSTTY_SCROLL_VIEWPORT_DELTA", "c": "int32" }, { "from": "delta", "c": "int64" }], "size": 24 }] },
    "scrollBottom": { "symbol": "ghostty_terminal_scroll_viewport", "arguments": [{ "self": true },
      { "struct": [{ "value": "GHOSTTY_SCROLL_VIEWPORT_BOTTOM", "c": "int32" }, { "value": 0, "c": "int64" }], "size": 24 }] } } },
  "RenderState": { "operations": {
    "populateRows": { "symbol": "ghostty_render_state_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR" },
      { "from": "rows", "c": "handle", "by": "pointer", "capability": "RenderStateRowIterator" }] },
    "cursorVisible": { "symbol": "ghostty_render_state_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_DATA_CURSOR_VISIBLE" }, { "out": "bool" }] },
    "cursorX": { "symbol": "ghostty_render_state_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_DATA_CURSOR_VIEWPORT_X" }, { "out": "uint16" }] } } },
  "RenderStateRowIterator": { "operations": {
    "populateCells": { "symbol": "ghostty_render_state_row_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_ROW_DATA_CELLS" },
      { "from": "cells", "c": "handle", "by": "pointer", "capability": "RenderStateRowCells" }] },
    "raw": { "symbol": "ghostty_render_state_row_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_ROW_DATA_RAW" }, { "out": "bits64" }] } } },
  "RenderStateRowCells": { "operations": {
    "raw": { "symbol": "ghostty_render_state_row_cells_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW" }, { "out": "bits64" }] },
    "graphemesLength": { "symbol": "ghostty_render_state_row_cells_get", "arguments": [{ "self": true }, { "value": "GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_LEN" }, { "out": "uint32" }] } } },
  "MouseEncoder": { "operations": {
    "setSize": { "symbol": "ghostty_mouse_encoder_setopt", "arguments": [{ "self": true }, { "value": "GHOSTTY_MOUSE_ENCODER_OPT_SIZE" },
      { "struct": [{ "sizeof": true, "c": "uint64" }, { "from": "screenWidth", "c": "uint32" }, { "from": "screenHeight", "c": "uint32" },
        { "from": "cellWidth", "c": "uint32" }, { "from": "cellHeight", "c": "uint32" }, { "from": "paddingTop", "c": "uint32" },
        { "from": "paddingBottom", "c": "uint32" }, { "from": "paddingRight", "c": "uint32" }, { "from": "paddingLeft", "c": "uint32" }], "by": "pointer" }] },
    "setAnyButtonPressed": { "symbol": "ghostty_mouse_encoder_setopt", "arguments": [{ "self": true }, { "value": "GHOSTTY_MOUSE_ENCODER_OPT_ANY_BUTTON_PRESSED" },
      { "from": "pressed", "c": "bool", "by": "pointer" }] } } },
  "KeyEvent": { "operations": {
    "setUtf8": { "symbol": "ghostty_key_event_set_utf8", "arguments": [{ "self": true }, { "from": "utf8", "c": "bytes", "retain": true }] } } },
  "MouseEvent": { "operations": {
    "setPosition": { "symbol": "ghostty_mouse_event_set_position", "arguments": [{ "self": true },
      { "struct": [{ "from": "x", "c": "float32" }, { "from": "y", "c": "float32" }] }] } } } } }
"""

    let private program = """using System;
using System.Linq;
using System.Text;
using Nyrwvy.Generated;
using static Nyrwvy.Generated.NyFacade;

int failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok) failures++;
}
static T Ok<T>(NyResult<T, NyStr> result, string what) => result.IsOk ? result.OkValue : throw new Exception(what + ": " + result.ErrorValue.Text);
static NyBytes Bytes(string text) => NyBytes.FromOwnedArray(Encoding.UTF8.GetBytes(text));
static string Text(NyBytes bytes) => Encoding.UTF8.GetString(bytes.Span);

var g = NewGhostty();
var term = Ok(ghosttyTerminalNew(g, 20, 5), "terminal_new");
var state = Ok(ghosttyRenderStateNew(g), "render_state_new");
var rows = Ok(ghosttyRenderStateRowIteratorNew(g), "row_iterator_new");
var cells = Ok(ghosttyRenderStateRowCellsNew(g), "row_cells_new");

terminalVtWrite(term, Bytes("hi\u001b[1;31mX\u001b[0m"));
Ok(renderStateUpdate(state, term), "render_state_update");
Check("cursor after text and an SGR sequence", Ok(renderStateCursorX(state), "cursor x") == 3);
Check("cursor visible", Ok(renderStateCursorVisible(state), "cursor visible"));
Ok(renderStatePopulateRows(state, rows), "row iterator");
Check("first row", renderStateRowIteratorNext(rows));
Check("row is not wrapped", !Ok(ghosttyRowWrap(g, Ok(renderStateRowIteratorRaw(rows), "row raw")), "row wrap"));
Ok(renderStateRowIteratorPopulateCells(rows, cells), "row cells");
var text = new StringBuilder();
while (renderStateRowCellsNext(cells))
{
    var cell = Ok(renderStateRowCellsRaw(cells), "cell raw");
    var codepoint = Ok(ghosttyCellCodepoint(g, cell), "codepoint");
    if (codepoint != 0) text.Append(char.ConvertFromUtf32((int)codepoint));
}
Check("codepoints read back", text.ToString() == "hiX");
var rowCount = 1;
while (renderStateRowIteratorNext(rows)) rowCount++;
Check("every row visited", rowCount == 5);

var keys = Ok(ghosttyKeyEncoderNew(g), "key_encoder_new");
var key = Ok(ghosttyKeyEventNew(g), "key_event_new");
keyEncoderSetoptFromTerminal(keys, term);
keyEventSetAction(key, 1);
keyEventSetKey(key, 0);
keyEventSetMods(key, 0);
keyEventSetUnshiftedCodepoint(key, 'a');
keyEventSetUtf8(key, Bytes("a"));
keyEventSetUtf8(key, Bytes("b"));
// The event keeps the pointer: the retained copy must survive a collection between set and encode.
for (var round = 0; round < 3; round++) { _ = new byte[1 << 20]; GC.Collect(); GC.WaitForPendingFinalizers(); }
Check("key event encodes the retained text", Text(Ok(keyEncoderEncode(keys, key), "key encode")) == "b");
Check("Int64 outside the enum's int range is a host failure", Fails<OverflowException>(() => keyEventSetKey(key, 1L << 40)));

terminalVtWrite(term, Bytes("\u001b[?1000h\u001b[?1006h"));
Check("mouse tracking on", Ok(terminalMouseTracking(term), "mouse tracking"));
var mice = Ok(ghosttyMouseEncoderNew(g), "mouse_encoder_new");
var mouse = Ok(ghosttyMouseEventNew(g), "mouse_event_new");
mouseEncoderSetoptFromTerminal(mice, term);
mouseEncoderSetSize(mice, 180, 90, 9, 18, 0, 0, 0, 0);
mouseEncoderSetAnyButtonPressed(mice, true);
mouseEventSetAction(mouse, 0);
mouseEventSetButton(mouse, 1);
mouseEventSetMods(mouse, 0);
mouseEventSetPosition(mouse, 2 * 9 + 1, 18 + 1);
Check("mouse event encodes as SGR", Text(Ok(mouseEncoderEncode(mice, mouse), "mouse encode")) == "\u001b[<0;3;2M");
mouseEventClearButton(mouse);

Check("plain paste is safe", ghosttyPasteIsSafe(g, Bytes("ls")));
Check("paste with a newline is not", !ghosttyPasteIsSafe(g, Bytes("ls\n")));
Check("bracketed paste retries past the 16-byte buffer", Text(Ok(ghosttyPasteEncode(g, Bytes("hello world"), true), "paste encode")) == "\u001b[200~hello world\u001b[201~");

Ok(terminalResize(term, 30, 10, 9, 18), "resize");
Ok(terminalSetColorForeground(term, 1, 2, 3), "set foreground");
for (var line = 0; line < 30; line++) terminalVtWrite(term, Bytes("line\r\n"));
terminalScrollDelta(term, -5);
Check("scrolled up", !Ok(terminalViewportActive(term), "viewport active"));
terminalScrollBottom(term);
Check("back at the bottom", Ok(terminalViewportActive(term), "viewport active"));

ghosttyMouseEventFree(g, mouse);
ghosttyMouseEncoderFree(g, mice);
ghosttyKeyEventFree(g, key);
ghosttyKeyEncoderFree(g, keys);
ghosttyRenderStateRowCellsFree(g, cells);
ghosttyRenderStateRowIteratorFree(g, rows);
ghosttyRenderStateFree(g, state);
ghosttyTerminalFree(g, term);
Check("released handles are closed", Fails<InvalidOperationException>(() => terminalVtWrite(term, Bytes("x")), "Handle is not open"));
return failures == 0 ? 0 : 1;

static bool Fails<T>(Func<object> action, string? message = null) where T : Exception
{
    try { action(); return false; }
    catch (T error) { return message is null || error.Message == message; }
}
"""

    let private run (directory: string) (environment: (string * string) list) (file: string) (arguments: string list) =
        let info = ProcessStartInfo(file)
        info.WorkingDirectory <- directory
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        info.Environment["DOTNET_NOLOGO"] <- "1"
        for name, value in environment do info.Environment[name] <- value
        for argument in arguments do info.ArgumentList.Add argument
        use child = Process.Start info
        let stderr = child.StandardError.ReadToEndAsync()
        let stdout = child.StandardOutput.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, stdout, stderr.Result

    let private succeed directory environment file arguments =
        match run directory environment file arguments with
        | 0, stdout, _ -> stdout
        | code, stdout, stderr -> failwithf "%s %A exited %d:\n%s\n%s" file arguments code stdout stderr

    let private vendor () =
        match Environment.GetEnvironmentVariable "NYRWVY_LIBGHOSTTY_VT" with
        | null | "" ->
            let rec up (directory: DirectoryInfo) =
                if isNull directory then None
                else
                    let candidate = Path.Combine(directory.FullName, "snap", "vendor", "libghostty-vt")
                    if Directory.Exists candidate then Some candidate else up directory.Parent
            up (DirectoryInfo(AppContext.BaseDirectory))
        | path -> Some path

    let private sendOk (session: AuthoringSession) (line: string) =
        use document = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        let reply = document.RootElement.Clone()
        if not (reply.GetProperty("ok").GetBoolean()) then failwithf "%s\n-> %s" (line.Substring(0, min 400 line.Length)) (reply.ToString())
        reply.GetProperty("result")

    let private commit session (line: string) =
        let proposal = (sendOk session line).GetProperty("proposalId").GetString()
        sendOk session $"""{{"id":"commit","op":"commit","proposalId":"{proposal}"}}""" |> ignore

    [<Fact; Trait("Suite", "Extended")>]
    let ``bind-c binds every libghostty function snap imports and drives the real library`` () =
        let library = vendor () |> Option.map (fun directory -> directory, Path.Combine(directory, "lib", "libghostty-vt.so"))
        let clang = try (let code, _, _ = run (Path.GetTempPath()) [] "clang" [ "--version" ] in code = 0) with _ -> false
        match library with
        | Some(directory, so) when clang && File.Exists so && File.Exists(Path.Combine(directory, "include", "ghostty", "vt.h")) ->
            let root = Path.Combine(Path.GetTempPath(), "nyrwvy-bind-ghostty-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory root |> ignore
            try
                File.WriteAllText(Path.Combine(root, "ghostty.spec.json"), spec)
                let tool = typeof<AuthoringSession>.Assembly.Location
                let releases =
                    nativeCsFunctions |> List.filter (fun name -> name.EndsWith "_free") |> List.collect (fun name -> [ "--release"; name ])
                let report =
                    succeed root [] "dotnet"
                        ([ tool; "bind-c"; "--header"; Path.Combine(directory, "include", "ghostty", "vt.h"); "--clang-arg"; "-I" + Path.Combine(directory, "include")
                           "--library"; "ghostty-vt"; "--capability"; "Ghostty"; "--effect"; "GhosttyCalls"; "--prefix"; "ghostty_"; "--type-prefix"; "Ghostty"
                           "--functions"; String.Join(",", nativeCsFunctions); "--spec"; "ghostty.spec.json"
                           "--failure"; "GhosttyResult=nonzero"; "--constant"; "allocator=null"; "--buffer"; "16:GHOSTTY_OUT_OF_SPACE"
                           "--requests"; "ghostty.requests.jsonl"; "--table"; "ghostty.csharp.json" ] @ releases)
                use reportDocument = JsonDocument.Parse report
                Assert.Equal(0, reportDocument.RootElement.GetProperty("unmapped").GetArrayLength())
                let table = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "ghostty.csharp.json")))
                let symbols =
                    [ for capability in table["capabilities"].AsObject() do
                          for operation in capability.Value["operations"].AsObject() do
                              yield operation.Value["symbol"].GetValue<string>() ] |> Set.ofList
                Assert.Equal<Set<string>>(Set.ofList nativeCsFunctions, symbols)

                use session = AuthoringSession.createNew (Path.Combine(root, "ghostty.json")) |> function Ok session -> session | Error error -> failwithf "%A" error
                let requests = File.ReadAllLines(Path.Combine(root, "ghostty.requests.jsonl"))
                for line in requests do commit session line
                // One exported forwarding function per operation, named <capability><Operation>, so the host can drive every binding.
                let functions =
                    [ for line in requests |> Array.skip 1 do
                          let capability = JsonNode.Parse line
                          let name = capability["displayName"].GetValue<string>()
                          let receiver = if name = "Ghostty" then "lib" else "self"
                          for operation in capability["operations"].AsArray() do
                              let op = operation["displayName"].GetValue<string>()
                              let parameters = operation["parameters"].AsArray()
                              let arguments =
                                  [ for parameter in parameters ->
                                        let parameterName = parameter["displayName"].GetValue<string>()
                                        JsonNode.Parse $"""{{"parameter":"{parameterName}","value":{{"kind":"parameter","name":"{parameterName}"}}}}""" ]
                              let functionName = string (Char.ToLowerInvariant name[0]) + name.Substring(1) + string (Char.ToUpperInvariant op[0]) + op.Substring(1)
                              let item =
                                  JsonNode.Parse
                                      $"""{{"displayName":"{functionName}","exported":true,"requiredEffects":["GhosttyCalls"],
                                           "parameters":[{{"displayName":"{receiver}","type":{{"kind":"capability","name":"{name}"}}}}],
                                           "body":{{"kind":"capabilityInvoke","capabilityValue":{{"kind":"parameter","name":"{receiver}"}},"capability":"{name}","operation":"{op}","arguments":[]}}}}"""
                              let declared = item["parameters"].AsArray()
                              let body = item["body"]
                              let passed = body["arguments"].AsArray()
                              for parameter in parameters do declared.Add(parameter.DeepClone())
                              for argument in arguments do passed.Add(argument)
                              item["resultType"] <- operation["resultType"].DeepClone()
                              yield item ]
                let batch = JsonObject()
                batch["id"] <- JsonValue.Create "functions"
                batch["op"] <- JsonValue.Create "simulateCreateFunctionsByName"
                batch["functions"] <- JsonArray(functions |> Array.ofList)
                commit session (batch.ToJsonString())

                let emit table = $"""{{"id":"emit","op":"emitCSharp","profile":{{"kind":"externalImplementations","operationIds":[],"tables":["{table}"]}},"output":"Generated.cs","facade":"Facade.cs"}}"""
                // The new argument forms are checked against the graph before emission.
                let generated = File.ReadAllText(Path.Combine(root, "ghostty.csharp.json"))
                let edit (path: string list) (change: JsonNode -> unit) =
                    let copy = JsonNode.Parse generated
                    change (path |> List.fold (fun (item: JsonNode) key -> match Int32.TryParse key with | true, index -> item[index] | _ -> item[key]) copy)
                    copy.ToJsonString()
                let operation capability name = [ "capabilities"; capability; "operations"; name ]
                for broken, expected in
                    [ generated.Replace("\"out\": \"bits64\"", "\"out\": \"float32\""), "does not convert"
                      generated.Replace("\"value\": 4,", "\"value\": 4294967296,"), "does not fit"
                      edit (operation "Ghostty" "cellCodepoint") (fun entry -> entry.AsObject().Remove "failure" |> ignore), "failure detection"
                      edit (operation "Ghostty" "pasteEncode") (fun entry -> entry.AsObject().Remove "failure" |> ignore), "failure"
                      edit (operation "Terminal" "scrollDelta" @ [ "arguments"; "1" ]) (fun item -> item["size"] <- JsonValue.Create 8), "natural size 16"
                      edit (operation "Ghostty" "pasteIsSafe" @ [ "arguments"; "0" ]) (fun item -> item["retain"] <- JsonValue.Create true), "retains"
                      edit (operation "KeyEncoder" "encode" @ [ "arguments"; "1" ]) (fun item -> item["release"] <- JsonValue.Create true), "cannot release" ] do
                    Assert.NotEqual<string>(generated, broken)
                    File.WriteAllText(Path.Combine(root, "broken.csharp.json"), broken)
                    use reply = AuthoringProtocol.executeLine session (emit "broken.csharp.json") |> JsonDocument.Parse
                    Assert.Contains(expected, reply.RootElement.GetProperty("error").GetProperty("message").GetString())
                sendOk session (emit "ghostty.csharp.json") |> ignore
                // Out values cross by address: a by-ref `out` makes P/Invoke build a marshalling stub per call.
                let source = File.ReadAllText(Path.Combine(root, "Generated.cs"))
                Assert.Contains("NyNative.Address(ref ", source)
                Assert.DoesNotMatch(@"extern [^\n]*\bout (?!nuint w)", source)
                File.WriteAllText(Path.Combine(root, "Program.cs"), program)
                File.WriteAllText(Path.Combine(root, "Smoke.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><LangVersion>preview</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
                succeed root [] "dotnet" [ "build"; "Smoke.csproj"; "-v:q" ] |> ignore
                let output = succeed root [ "LD_LIBRARY_PATH", Path.GetDirectoryName so ] "dotnet" [ Path.Combine(root, "bin", "Debug", "net10.0", "Smoke.dll") ]
                Assert.DoesNotContain("FAIL", output)
                Assert.Equal(16, output.Split('\n') |> Array.filter (fun line -> line.StartsWith "ok  ") |> Array.length)
            finally
                if Directory.Exists root then Directory.Delete(root, true)
        | _ ->
            Console.WriteLine "SKIPPED libghostty bind-c acceptance: set NYRWVY_LIBGHOSTTY_VT to a libghostty-vt directory (include/, lib/libghostty-vt.so) and install clang."
