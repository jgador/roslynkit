import { FormatterBase } from "./contracts.js";

/** Formats user records for display in the application. */
export class UserFormatter extends FormatterBase<User> {
  format(value: User): string;
  format(value: User, prefix: string): string;
  format(value: User, prefix = "user"): string {
    const normalized = this.normalize(value);
    return `${prefix}:${normalized.name}`;
  }
}

/** Represents a user accepted by the formatting pipeline. */
export interface User {
  id: number;
  name: string;
}

/** Returns the input value while preserving its generic type. */
export function identity<T>(value: T): T {
  return value;
}
