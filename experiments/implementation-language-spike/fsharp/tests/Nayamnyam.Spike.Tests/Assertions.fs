namespace Nayamnyam.Spike.Tests

open System

module Assertions =
    let fail message =
        raise (Exception message)

    let equal expected actual message =
        if expected <> actual then
            fail $"{message} Expected: {expected}. Actual: {actual}."

    let isTrue condition message =
        if not condition then
            fail message

    let same expected actual message =
        if not (Object.ReferenceEquals(expected, actual)) then
            fail message

    let throwsArgument action message =
        try
            action ()
            fail message
        with
        | :? ArgumentException -> ()
        | exceptionValue ->
            raise (Exception($"{message} Wrong exception type.", exceptionValue))
