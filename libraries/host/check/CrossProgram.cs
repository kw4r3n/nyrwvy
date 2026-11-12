// The cross-backend lock check's driver: `hold <path> <ms>` takes the lock through the emitted facade,
// sleeps inside the UseResource body and prints "held"; `try <path>` prints "ok" or "error: ...".
// Used by run-cross.mjs, which runs it against the same lock file as the Zig build of the same program.
using System;
using NyrHost;
using Nyrwvy.Generated;

var locks = new LocksHost();
var clock = new ClockHost();
var result = args[0] switch
{
    "hold" => NyFacade.holdLock(locks, clock, args[1], long.Parse(args[2])),
    "try" => NyFacade.tryLock(locks, args[1]),
    _ => throw new ArgumentException("mode must be hold or try"),
};
Console.WriteLine(result.Text);
