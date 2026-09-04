// Mirrors src/Aperture.SharedKernel/Authorization/Permissions.cs, constant for constant.
// Hand-kept until the OpenAPI document is published and this file is generated from it.
// The UI disables what the user cannot do — the server denies it anyway. This list is
// convenience, never security.
export const Permissions = {
  AccountsRead: 'accounts.read',
  AccountsWrite: 'accounts.write',
  ContactsRead: 'contacts.read',
  ContactsWrite: 'contacts.write',
  DealsRead: 'deals.read',
  DealsWrite: 'deals.write',
  DealsDiscountApprove: 'deals.discount.approve',
  OrdersRead: 'orders.read',
  OrdersWrite: 'orders.write',
  OrdersConfirm: 'orders.confirm',
  OrdersCreditOverride: 'orders.credit.override',
  TimelineRead: 'timeline.read',
  TimelineWrite: 'timeline.write',
  AssistantUse: 'assistant.use',
  AdminUsers: 'admin.users',
  AdminIntegrations: 'admin.integrations',
  AuditRead: 'audit.read',
} as const;

export type Permission = (typeof Permissions)[keyof typeof Permissions];
