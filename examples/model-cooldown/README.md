# Model cooldown

rrawrlet's model cooldown update (`Paths.Cooldown` in its `Host.cs`) ported to a Nyrwvy graph program. It is the worked example for the host library's persistent-state primitives (`Fs.rename`, `Fs.exists`, `Clock`, `Locks`/`Lock`): the lock, the read, the decision, the atomic replace and the corrupt-file set-aside are all written in the graph, and only the single OS operations stay in the host.

`coolModel(fs, clock, locks, dir, model, seconds, reason)` updates `<dir>/model-cooldown.json`:

1. Takes `<dir>/model-cooldown.json.lock` through `Locks.acquire`, retrying every 50 ms up to 101 attempts (the original's `Hold`); the lock is held in a `UseResource` section, so every exit releases it.
2. Reads the file if it exists and parses it with the JSON library. A parse failure moves the file to `model-cooldown.json.bad` (replacing an older one) and starts empty; valid JSON that is not an object also starts empty, without the move.
3. With `seconds <= 0` the model's entry is removed, and if it was not there nothing is written. Otherwise the entry is replaced: `until` is the larger of `now + seconds` and the existing `until`, `since` is kept while the old entry is still active and is `now` otherwise, and `reason` has every run of U+001F, CR and LF collapsed to one space.
4. Entries whose `until` has passed are dropped, the JSON is serialized, written to `model-cooldown.json.tmp` and renamed over the file.

The result is `Ok("")`, or `Ok("corrupt file moved to model-cooldown.json.bad")` when step 2 moved a file (the original writes that notice to standard error instead). An I/O failure inside the section, or a lock that stays held past the retries, is the result's `Err`; the lock is still released.

## Differences from the original

Accepted for the port, as recorded in the implementation handoff:

- The temporary file has a fixed name (`model-cooldown.json.tmp`); the original adds a GUID. One writer holds the lock, so the name cannot collide.
- The write is not synced; the original flushes with `Flush(true)`.
- The directory must already exist; the original creates it.
- The corrupt-file notice is returned, not printed.
- Updating an existing model moves its entry to the end of the object; the original keeps its position. The file reads as the same content either way, but the bytes differ.
- A field whose number is written as `1.0` or `1e3` counts as the integer it equals; the original accepts only integer spellings and reads the rest as 0.

The file format is unchanged, so the original code and this program read each other's files.

## Running it

From the repository root:

```sh
npm ci
npm run build:host
node examples/model-cooldown/run-zig.mjs
node examples/model-cooldown/run.mjs
```

`run-zig.mjs` emits a Zig library and drives `coolModel` against the real file system from a Zig test; `run.mjs` emits C# and runs `Program.cs` against the real host classes. Both check the same scenarios: starting from no file, `until` never lowered, `since` kept and restarted, removal, the expiry sweep, the corrupt-file move, the reason cleanup, and a held lock retried until it frees. Neither script is part of the Node test suite, as with `libraries/host/check/run.mjs`.
