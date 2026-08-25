import { makeKey, normalize } from "./shared.mjs";

export async function* input(records) {
  for (const record of records) {
    const { id, payload } = normalize(record);
    yield Promise.resolve({ id, payload, factor: id.length });
  }
}

export function* archive(events, prefix = "archive") {
  for (const [index, event] of events.entries()) {
    yield { index, event, key: makeKey(prefix, index) };
  }
}
