// Reuse the established token/close audits, with only snapshot containment varied.
import {author as audited} from '../member-close-intent/author.mjs'
export const author=(s,{containSnapshotFaults=true}={})=>audited(s,{retainCloseIntent:true,containSnapshotFaults})
