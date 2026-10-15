import {author as ownedAuthor} from '../offline-session-owned-io/author.mjs'
export const maxFrameBytes=16*1024
// Policy is a literal in the semantic graph's capability invocation. Framing,
// byte accounting and strict decoding are the external transport's contract.
export const author=(s,{relaxed=false}={})=>ownedAuthor(s,{maxFrameBytes:relaxed?2*1024*1024:maxFrameBytes})
