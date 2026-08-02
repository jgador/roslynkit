import { UserFormatter, identity, type User } from "./formatters.js";

const formatter = new UserFormatter();
const user: User = identity({ id: 1, name: "Ada" });
export const formattedUser = formatter.format(user, "member");

export function UserLabel() {
  return <span>{formattedUser}</span>;
}
