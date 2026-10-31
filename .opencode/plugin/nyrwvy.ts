import type { Plugin } from "@opencode-ai/plugin"
import { closeAuthoring } from "../../tools/nyrwvy-agent/authoring.ts"

export const NyrwvyPlugin: Plugin = async ({ worktree }) => ({
  event: async ({ event }) => {
    if (event.type === "session.deleted") {
      try {
        await closeAuthoring({ worktree, sessionID: event.properties.info.id })
      } catch (error) {
        console.error("Failed to close deleted session's Nyrwvy authoring process:", error)
      }
    }
  },
})
