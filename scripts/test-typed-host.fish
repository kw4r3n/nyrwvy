#!/usr/bin/env fish
exec node (path resolve (dirname (status filename))/test-typed-host.mjs) $argv
