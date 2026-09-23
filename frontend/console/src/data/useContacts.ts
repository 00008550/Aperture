import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { createContact, departContact, listContacts, type CreateContactRequest } from '../api';
import { useAccessToken } from '../auth';
import { Permissions } from '../permissions';
import { deniedLocally, toGridModel, useGate } from './gate';
import { queryKeys } from './keys';

/**
 * Contacts server-state. The API exposes no single-contact GET (ContactEndpoints.cs maps list,
 * create-under-account and depart only), so there is no detail hook — adding one would mean a new
 * endpoint, which this portion does not do. Both writes invalidate the `contacts` namespace.
 */

interface ContactsListOptions {
  limit?: number | undefined;
  includeDeparted?: boolean | undefined;
}

export function useContacts(options: ContactsListOptions = {}) {
  const token = useAccessToken();
  const { session, allowed } = useGate(Permissions.ContactsRead);
  const params = { limit: options.limit, includeDeparted: options.includeDeparted };

  const query = useInfiniteQuery({
    queryKey: queryKeys.contacts.list(token, params),
    queryFn: ({ pageParam }) => listContacts({ ...params, cursor: pageParam }),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    enabled: token !== null && allowed,
    retry: false,
  });

  const grid = toGridModel({
    allowed,
    scopeCount: session.data?.scopes.length,
    pages: query.data?.pages,
    isPending: query.isPending,
    error: query.error,
    hasNextPage: query.hasNextPage,
  });

  return { ...query, grid };
}

export function useCreateContact() {
  const queryClient = useQueryClient();
  const { allowed } = useGate(Permissions.ContactsWrite);

  return useMutation({
    mutationFn: ({ accountId, body }: { accountId: string; body: CreateContactRequest }) =>
      allowed
        ? createContact(accountId, body)
        : Promise.reject(deniedLocally(Permissions.ContactsWrite)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.contacts.all }),
  });
}

export function useDepartContact() {
  const queryClient = useQueryClient();
  const { allowed } = useGate(Permissions.ContactsWrite);

  return useMutation({
    mutationFn: (id: string) =>
      allowed ? departContact(id) : Promise.reject(deniedLocally(Permissions.ContactsWrite)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.contacts.all }),
  });
}
