import {writeFile} from 'node:fs/promises'
import {scenarios} from './cases.mjs'
import {request as q} from '../member-lifecycle/cases.mjs'
const c=scenarios().find(c=>c.kind==='wait-stop')
c.script=[...c.script.slice(0,6),q('integrate','first',1),q('stop','stopping',2),q('gate','commit-release')]
await writeFile(`${process.argv[2]}/cases.json`,JSON.stringify([c],null,2))
