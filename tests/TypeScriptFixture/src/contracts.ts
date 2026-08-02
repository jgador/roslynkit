/** Describes a service that formats application values. */
export interface Formatter<T> {
  format(value: T): string;
}

/** Provides the shared base behavior for value formatters. */
export abstract class FormatterBase<T> implements Formatter<T> {
  abstract format(value: T): string;

  protected normalize(value: T): T {
    return value;
  }
}
