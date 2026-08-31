import { expect, test } from "bun:test"
import pluginModule from "./nyamnyam-eval"

test("M1 evaluation plugin exports the OpenCode v1 server module shape", () => {
  expect(pluginModule.id).toBe("nyamnyam-eval")
  expect(typeof pluginModule.server).toBe("function")
})
