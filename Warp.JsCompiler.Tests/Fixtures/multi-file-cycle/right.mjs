import { cache, key } from "./common.mjs";
import "./left.mjs";

export function* expand(records = []) {
  let index = 0;
  outer: for (const [name, value = 0, tags = []] of records) {
    switch (name) {
      case "skip":
        continue outer;
      case "stop":
        break outer;
      default:
        cache.set(key(name, index++), value);
        yield { name, value, tags };
    }
  }
}
