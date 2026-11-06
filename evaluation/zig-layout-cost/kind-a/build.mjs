// Builds two libraries from a scratch copy of Rhifill's app/nyr (the original is only read):
//   base/liblayout.so  exports layoutFrame and layoutFrameFlat, both running the Nyr body;
//   kinda/liblayout.so the same, but layoutFrameFlat's body is replaced by layoutframe.zig through a mapping table.
// layoutFrame itself cannot be bound: its CellGrid parameter is a Product, and a host declaration takes no Product
// argument. layoutFrameFlat(rows, narrow, wide) is a scratch-only wrapper that builds the CellGrid and calls layoutFrame.
// Usage: node build.mjs /path/to/rhifill/app/nyr /new/empty/out-dir    (needs tools/zig/host/bin/nyrwvy-host, zig)
// Env: NYRWVY_ZIG (absolute zig path if the shim fails outside a project), NYRWVY_REPO (default: this checkout), NYRWVY_EXPECT_BIND_FAIL=1 prints the refusal of binding layoutFrame itself.
import {cp, mkdir, writeFile, readFile, readdir} from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import {join, resolve, dirname} from 'node:path';
import {pathToFileURL, fileURLToPath} from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const nyrwvy = resolve(process.env.NYRWVY_REPO ?? join(here, '../../..'));
const [source, out] = process.argv.slice(2).map(p => resolve(p));
if (!source || !out) throw Error('usage: node build.mjs <rhifill app/nyr> <empty out dir>');
const {openAuthoring, requestAuthoring, closeAuthoring} = await import(pathToFileURL(join(nyrwvy, 'tools/nyrwvy-agent/authoring.ts')).href);

const i64 = {kind: 'primitive', primitive: 'int64'};
const rowsType = {kind: 'builtinGeneric', generic: 'list', arguments: [{kind: 'builtinGeneric', generic: 'list', arguments: [i64]}]};
const wrapper = {
    kind: 'createFunction', displayName: 'layoutFrameFlat', requiredEffects: [], resultType: rowsType,
    parameters: [{displayName: 'rows', type: rowsType}, {displayName: 'narrow', type: i64}, {displayName: 'wide', type: i64}],
    body: {kind: 'directCall', function: 'layoutFrame', arguments: [
        {parameter: 'rows', value: {kind: 'parameter', name: 'rows'}},
        {parameter: 'grid', value: {kind: 'productConstruct', type: 'CellGrid', fields: [
            {field: 'narrow', value: {kind: 'parameter', name: 'narrow'}}, {field: 'wide', value: {kind: 'parameter', name: 'wide'}}]}}]},
};

async function session(dir, label, steps) {
    const context = {sessionID: 'kind-a-' + label + '-' + process.pid, worktree: nyrwvy};
    const request = async body => JSON.parse(await requestAuthoring(body, context));
    try {
        await openAuthoring('open', join(dir, 'workspace.json'), context);
        if (steps) {
            const review = await request({op: 'simulateBatchByName', steps: [steps]});
            if (!review.ok) throw Error('wrapper: ' + JSON.stringify(review.error));
            const committed = await request({op: 'commit', proposalId: review.result.proposalId});
            if (!committed.ok) throw Error('commit: ' + JSON.stringify(committed.error));
        }
        return await request({op: 'emitZigLibrary', output: 'layout.zig'});
    } finally {
        await closeAuthoring(context).catch(() => {});
    }
}

function build(dir) {
    const r = spawnSync(process.env.NYRWVY_ZIG || 'zig', ['build-lib', '-dynamic', '-lc', '-O', 'ReleaseSafe', 'layout.zig', '-femit-bin=liblayout.so'],
        {cwd: dir, encoding: 'utf8', stdio: 'inherit', timeout: 900000});
    if (r.status !== 0) throw Error('zig build-lib failed in ' + dir);
}

async function copyNyr(to) {
    await cp(source, to, {recursive: true, filter: p => !/\/(zig|ghostty)(\/|$)|\.lock$|\.lease$/.test(p)});
}

// base: the wrapper is committed into the scratch copy; the copy's snapshot is then reused for kind A.
const base = join(out, 'base');
await mkdir(base, {recursive: true});
await copyNyr(base);
await writeFile(join(base, 'workspace.json.backends.json'), JSON.stringify({targets: ['csharp'], exports: ['layoutFrame', 'layoutFrameFlat']}));
const baseReply = await session(base, 'base', wrapper);
build(base);

const kinda = join(out, 'kinda');
await mkdir(kinda, {recursive: true});
// The snapshot now holds the wrapper; its sidecars come from the original copy.
await cp(base, kinda, {recursive: true, filter: p => !/layout\.(zig|so)|\.lock$|\.lease$/.test(p)});
await cp(join(here, 'layoutframe.zig'), join(kinda, 'layoutframe.zig'));
await writeFile(join(kinda, 'layoutframe.zig.json'), JSON.stringify({
    format: 'nyrwvy.zig-mapping', version: 1, source: 'layoutframe.zig',
    functions: {layoutFrameFlat: {declaration: 'layoutFrameFlat'}},
}, null, 2));
await writeFile(join(kinda, 'workspace.json.backends.json'), JSON.stringify({
    targets: ['csharp'], exports: ['layoutFrame', 'layoutFrameFlat'], zigTables: ['layoutframe.zig.json']}));
const kindaReply = await session(kinda, 'kinda', null);
if (!kindaReply.ok) throw Error('kind A emission: ' + JSON.stringify(kindaReply.error));
build(kinda);
console.log('exports', baseReply.result.exports.map(e => e.symbol).join(' '), '| built', join(base, 'liblayout.so'), join(kinda, 'liblayout.so'));

if (process.env.NYRWVY_EXPECT_BIND_FAIL) {
    // Binding layoutFrame itself (Product parameter) must be refused; recorded, not worked around.
    const bad = join(out, 'bindfail');
    await mkdir(bad, {recursive: true});
    await cp(kinda, bad, {recursive: true, filter: p => !/layout\.(zig|so)/.test(p)});
    await writeFile(join(bad, 'layoutframe.zig.json'), JSON.stringify({
        format: 'nyrwvy.zig-mapping', version: 1, source: 'layoutframe.zig', functions: {layoutFrame: {declaration: 'layoutFrame'}}}));
    console.log('bind layoutFrame:', JSON.stringify(await session(bad, 'bindfail', null)));
}
