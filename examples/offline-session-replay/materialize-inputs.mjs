import {materializeInputs} from './retained-inputs.mjs'
if(process.argv.length!==3)throw new Error('Usage: node examples/offline-session-replay/materialize-inputs.mjs <fresh-input-directory>')
const receipt=await materializeInputs(process.argv[2])
console.log(`PASS: ${receipt.files.length} exact retained input files materialized and authenticated`)
