const registry = new Map();

const keyFor = (prefix, value) => `${prefix}:${value}`;

class BaseChannel {
  #events = [];

  constructor(name, options = {}) {
    this.name = name;
    this.options = options;
  }

  get events() {
    return this.#events;
  }

  set events(value) {
    this.#events = value;
  }

  ["record"](event) {
    this.#events.push(event);
    return event;
  }

  static create(name, options) {
    return new this(name, options);
  }
}

class DerivedChannel extends BaseChannel {
  #pending = new Map();
  #sequence = 0;
  label = "channel";
  static version = 1;

  constructor(name, options = {}) {
    super(name, options);
    this.label = `${name}:${options.mode || "default"}`;
  }

  async *consume(entries) {
    let completed = 0;
    outer: for await (const {
      id,
      payload: { value = 0, tags = [] } = {},
      ...meta
    } of entries) {
      const token = ++this.#sequence;
      const snapshot = () => ({ token, id, value, tags, meta, label: this.label });
      this.#pending.set(token, snapshot);
      try {
        if (meta.skip) continue;
        if (meta.stop) break outer;
        const result = await this.transform(id, value, tags, meta);
        this["record"]({ id, result, token });
        completed++;
        yield { id, result, completed, snapshot };
      } catch ({ message = "unknown", code = "E_UNKNOWN", ...details }) {
        this["record"]({ id, code, message, details });
        yield { id, error: code, details, snapshot };
      } finally {
        this.#pending.delete(token);
        registry.set(keyFor(this.name, token), completed);
      }
    }
    return { completed, pending: this.#pending.size };
  }

  async transform(id, value, tags, meta) {
    const factor = meta.factor ?? 1;
    const derived = await Promise.resolve(value * factor + tags.length);
    return super["record"]({ id, derived, factor }).derived;
  }

  static {
    this.version += 1;
  }
}

class AuditChannel extends DerivedChannel {
  #audit = [];
  #lastError = null;
  static [keyFor("audit", "version")] = 1;

  get lastError() {
    return this.#lastError;
  }

  set lastError(value) {
    this.#lastError = value;
  }

  *history(prefix = this.name) {
    for (const [index, event] of this.events.entries()) {
      yield { index, event, key: keyFor(prefix, index) };
    }
  }

  async *replay({
    records = [],
    policy: { retries = 0, enabled = true, ...policyMeta } = {},
    ...requestMeta
  } = {}) {
    const context = { retries, enabled, policyMeta, requestMeta };
    const makeSnapshot = () => ({ context, size: this.events.length, name: this.name });
    let attempt = 0;
    retry: while (attempt++ <= retries) {
      try {
        if (!enabled) break retry;
        for await (const entry of this.consume(source(records))) {
          const { id, result = null, error = null, ...rest } = entry;
          if (error) {
            this.lastError ??= error;
            yield { id, error, rest, snapshot: makeSnapshot() };
            continue;
          }
          super["record"]({ id, result, attempt, rest });
          yield { id, result, attempt, snapshot: makeSnapshot() };
        }
        break retry;
      } catch (error) {
        const { message = "replay", ...details } = error;
        this.lastError = message;
        super["record"]({ message, details, attempt });
        if (attempt > retries) throw error;
      } finally {
        registry.set(keyFor(this.name, "attempt"), attempt);
      }
    }
    yield* this.history("replay");
  }
}

async function* source(records) {
  for (const [id, value = 0, tags = []] of records) {
    yield Promise.resolve({ id, payload: { value, tags }, factor: id.length });
  }
}

export async function execute(records, { name = "main", mode = "batch" } = {}) {
  const channel = AuditChannel.create(name, { mode });
  const output = [];
  try {
    for await (const entry of channel.replay({
      records,
      policy: { retries: mode === "retry" ? 2 : 0, enabled: true, mode }
    })) {
      output.push(entry);
    }
  } finally {
    registry.set(name, channel.events.length);
  }
  return {
    output,
    events: channel.events,
    history: [...channel.history()],
    version: DerivedChannel.version,
    auditVersion: AuditChannel[keyFor("audit", "version")]
  };
}

export { DerivedChannel, AuditChannel, registry };
