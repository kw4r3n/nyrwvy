namespace Nyrwvy.Core.Tests

open System
open System.Text.RegularExpressions

[<AutoOpen>]
module Utf16Literal =
    /// F# compiles an unpaired surrogate escape such as "\uD800" in a string literal to U+FFFD, so
    /// test Strings spell each UTF-16 code unit that must survive as `{D800}` (four uppercase hex
    /// digits) and build the String at run time.
    let utf16 (text: string) =
        Regex.Replace(text, @"\{([0-9A-F]{4})\}", fun item -> string (char (Convert.ToInt32(item.Groups[1].Value, 16))))
