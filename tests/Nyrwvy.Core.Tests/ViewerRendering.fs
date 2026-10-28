namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.Net
open System.Text
open System.Text.Json
open Nyrwvy.Core

/// Renders viewer pages as a browser would, by running the page's own script in Node, so tests
/// assert on the markup a reader sees.
module ViewerRendering =
    let private script =
        """
const readline = require('node:readline');
const vm = require('node:vm');
readline.createInterface({ input: process.stdin }).on('line', line => {
  let reply;
  try {
    const page = JSON.parse(line);
    const code = page.indexOf('<script>\n') + 9;
    const dataStart = page.indexOf('id="nyrwvy-data">') + 17;
    const context = vm.createContext({});
    vm.runInContext(page.slice(code, page.indexOf('</script>', code)), context);
    const result = vm.runInContext('NyrwvyViewer', context).render(JSON.parse(page.slice(dataStart, page.indexOf('</script>', dataStart))));
    reply = { title: result.title, body: result.body };
  } catch (error) { reply = { error: String(error && error.stack || error) }; }
  process.stdout.write(JSON.stringify(reply) + '\n');
});
"""

    let private gate = obj ()

    let private node =
        lazy
            (let info = ProcessStartInfo("node", [| "-e"; script |])
             info.RedirectStandardInput <- true
             info.RedirectStandardOutput <- true
             info.StandardOutputEncoding <- UTF8Encoding(false)
             info.UseShellExecute <- false
             Process.Start info)

    /// The page's title element and body markup, as its script renders them.
    let page (content: string) : string =
        lock gate (fun () ->
            let rendering = node.Value
            rendering.StandardInput.WriteLine(JsonSerializer.Serialize content)
            rendering.StandardInput.Flush()
            use reply = JsonDocument.Parse(rendering.StandardOutput.ReadLine())
            match reply.RootElement.TryGetProperty "error" with
            | true, error -> failwith ("The viewer script failed: " + error.GetString())
            | _ -> "<title>" + WebUtility.HtmlEncode(reply.RootElement.GetProperty("title").GetString()) + "</title>\n" + reply.RootElement.GetProperty("body").GetString())

    let focus (model: ViewerFocusModel) : string = page (ViewerHtml.page (ViewerData.ofFocus model))

    let changeReview (model: ViewerChangeReviewModel) : string = page (ViewerHtml.page (ViewerData.ofChangeReview true model))
