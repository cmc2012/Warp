import { makeKey, registry } from "./shared.mjs";
import { WorkerLedger } from "./worker.mjs";
import { archive, input } from "./archive.mjs";

export async function execute(records, { name = "multi", retries = 1 } = {}) {
  const worker = WorkerLedger.create(name, { retries });
  const output = [];
  const history = [];
  try {
    for await (const entry of worker.process(input(records), { retries, source: "entry" })) {
      output.push(entry);
    }
  } finally {
    registry.set(makeKey(name, "records"), worker.records.length);
  }
  for (const item of archive(worker.records, name)) {
    history.push(item);
  }
  return {
    output,
    history,
    version: WorkerLedger[makeKey("worker", "version")]
  };
}

export { WorkerLedger, registry };
