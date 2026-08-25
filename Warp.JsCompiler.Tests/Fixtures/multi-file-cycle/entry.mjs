import { cache, key } from "./common.mjs";
import { LeftProcessor } from "./left.mjs";
import { expand } from "./right.mjs";

export async function coordinate(records, { name = "cycle", ...options } = {}) {
  const processor = new LeftProcessor(name);
  const output = [];
  try {
    for (const record of expand(records)) {
      output.push(await processor.process({ ...record, ...options }));
    }
  } catch ({ message = "coordinate", ...detail }) {
    output.push({ message, detail });
  } finally {
    cache.set(key(name, "final"), output.length);
  }
  return { output, entries: [...processor.entries()] };
}

export { cache };
