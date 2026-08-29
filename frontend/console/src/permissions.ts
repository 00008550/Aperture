// Mirrors src/Aperture.SharedKernel/Authorization/Permissions.cs.
// Generated from the OpenAPI document once 001-P3 lands; hand-kept until then.
// The UI hides what the user cannot do — the server denies it anyway. This list is
// convenience, never security.
export const Permissions = {
  AccountsRead: 'accounts.read',
  DealsRead: 'deals.read',
  DealsWrite: 'deals.write',
  OrdersRead: 'orders.read',
  OrdersConfirm: 'orders.confirm',
  OrdersCreditOverride: 'orders.credit.override',
  AdminUsers: 'admin.users',
} as const;

export type Permission = (typeof Permissions)[keyof typeof Permissions];
