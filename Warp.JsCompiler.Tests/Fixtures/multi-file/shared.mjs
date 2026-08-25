export const registry = new Map();
export const makeKey = (group, value) => `${group}:${value}`;

export class BaseLedger {
  #records = [];

  constructor(name, options = {}) {
    this.name = name;
    this.options = options;
  }

  get records() { return this.#records; }
  set records(value) { this.#records = value; }

  ["append"](record) {
    this.#records.push(record);
    return record;
  }

  static create(name, options) {
    return new this(name, options);
  }
}

export function normalize([id, value = 0, tags = []]) {
  return { id, payload: { value, tags } };
}
