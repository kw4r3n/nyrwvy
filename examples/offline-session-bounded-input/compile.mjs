import {readFile} from 'node:fs/promises'
import {compile} from '../offline-session-owned-io/compile.mjs'
const inherited=await readFile(new URL('../offline-session-owned-io/pipe.template.cs',import.meta.url),'utf8')
const pipeTemplate=inherited.split('sealed class RecordedInput')[0]+await readFile(new URL('./pipe.template.cs',import.meta.url),'utf8')+inherited.slice(inherited.indexOf('static class Program'))
await compile(process.argv[2],{pipeTemplate})
