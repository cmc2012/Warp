export const cache = new Map();
export const key = (scope, value) => `${scope}/${value}`;

export function select({ value = 0, tags = [], ...meta } = {}) {
  return { value, tags, meta, score: value + tags.length };
}
