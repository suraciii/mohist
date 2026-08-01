import { useEffect, useMemo, useRef, useState } from 'react'
import { Loader2Icon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { ACCESS_POLICY_VALUES } from '../../../entities/agent-connection'
import type { AccessPolicyKind, SlackMemberSearchEntry } from '../../../entities/agent-connection'
import { CardSection } from '@/shared/ui/components/card-section'

export interface AccessPolicySubmitInput {
  accessPolicy: AccessPolicyKind
  allowMembers: string[]
}

interface AccessPolicySectionProps {
  accessPolicy: string
  allowMembers: string[]
  ownerSlackUserId: string | null
  anyoneDisclosure: string
  onSubmit: (input: AccessPolicySubmitInput) => void
  isSubmitting: boolean
  errorMessage: string | null
  searchMembers: (query: string) => Promise<SlackMemberSearchEntry[]>
}

const POLICY_LABELS: Record<string, string> = {
  owner_only: 'Owner only',
  allowlist: 'Allowlist',
  anyone: 'Anyone',
}

const POLICY_DESCRIPTIONS: Record<string, string> = {
  owner_only: 'Only the Connection Owner may invoke the Agent.',
  allowlist: 'The Owner and named workspace members may invoke the Agent.',
  anyone: 'Any current regular workspace member in a channel the Bot is in may invoke the Agent.',
}

function normalizePolicy(value: string): AccessPolicyKind {
  return ACCESS_POLICY_VALUES.includes(value as AccessPolicyKind)
    ? (value as AccessPolicyKind)
    : 'owner_only'
}

export function AccessPolicySection({
  accessPolicy,
  allowMembers,
  ownerSlackUserId,
  anyoneDisclosure,
  onSubmit,
  isSubmitting,
  errorMessage,
  searchMembers,
}: AccessPolicySectionProps) {
  const [policy, setPolicy] = useState<AccessPolicyKind>(() => normalizePolicy(accessPolicy))
  const [members, setMembers] = useState<string[]>(() => allowMembers)
  const [confirmedAnyone, setConfirmedAnyone] = useState(false)
  const [searchQuery, setSearchQuery] = useState('')
  const [searchResults, setSearchResults] = useState<SlackMemberSearchEntry[]>([])
  const [isSearching, setIsSearching] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)

  const lastSyncedPolicy = useRef(accessPolicy)
  const lastSyncedMembers = useRef(allowMembers.join(','))

  useEffect(() => {
    if (accessPolicy !== lastSyncedPolicy.current) {
      lastSyncedPolicy.current = accessPolicy
      setPolicy(normalizePolicy(accessPolicy))
      setConfirmedAnyone(false)
    }
  }, [accessPolicy])

  useEffect(() => {
    const snapshot = allowMembers.join(',')
    if (snapshot !== lastSyncedMembers.current) {
      lastSyncedMembers.current = snapshot
      setMembers(allowMembers)
    }
  }, [allowMembers])

  useEffect(() => {
    return () => {
      setSearchQuery('')
      setSearchResults([])
    }
  }, [])

  useEffect(() => {
    if (policy !== 'allowlist') setMembers([])
  }, [policy])

  async function runSearch(rawQuery: string) {
    const query = rawQuery.trim()
    if (query.length === 0) {
      setSearchResults([])
      setSearchOpen(false)
      return
    }
    setIsSearching(true)
    setSearchOpen(true)
    try {
      const results = await searchMembers(query)
      setSearchResults(results)
    } catch {
      setSearchResults([])
    } finally {
      setIsSearching(false)
    }
  }

  const searchTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  function onSearchChange(next: string) {
    setSearchQuery(next)
    if (searchTimer.current) clearTimeout(searchTimer.current)
    searchTimer.current = setTimeout(() => void runSearch(next), 250)
  }

  function addMember(member: SlackMemberSearchEntry) {
    if (member.slackUserId === ownerSlackUserId) return
    setMembers((prev) =>
      prev.includes(member.slackUserId) ? prev : [...prev, member.slackUserId],
    )
    setSearchQuery('')
    setSearchResults([])
    setSearchOpen(false)
  }

  function removeMember(id: string) {
    setMembers((prev) => prev.filter((m) => m !== id))
  }

  const canSubmit = useMemo(() => {
    if (isSubmitting) return false
    if (policy === 'anyone' && !confirmedAnyone) return false
    return true
  }, [isSubmitting, policy, confirmedAnyone])

  function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canSubmit) return
    onSubmit({ accessPolicy: policy, allowMembers: policy === 'allowlist' ? members : [] })
  }

  return (
    <CardSection title="Channel access policy" data-testid="connection-access-policy-section">
      <form onSubmit={handleSubmit} className="space-y-3" data-testid="connection-access-policy-form">
        <p className="text-xs text-muted-foreground">
          Decides who may invoke this Bot in a channel. Direct messages stay Owner-only under every policy.
        </p>
        <fieldset className="space-y-2" disabled={isSubmitting}>
          <legend className="sr-only">Access policy</legend>
          {ACCESS_POLICY_VALUES.map((value) => (
            <label
              key={value}
              className="flex cursor-pointer items-start gap-2 rounded-md border border-border px-3 py-2 text-sm has-checked:border-info has-checked:bg-info-subtle"
              data-testid={`connection-access-policy-option-${value}`}
            >
              <input
                type="radio"
                name="access-policy"
                value={value}
                checked={policy === value}
                onChange={() => setPolicy(value)}
                className="mt-0.5"
                data-testid={`connection-access-policy-radio-${value}`}
              />
              <span className="space-y-0.5">
                <span className="block font-medium text-foreground">{POLICY_LABELS[value]}</span>
                <span className="block text-xs text-muted-foreground">{POLICY_DESCRIPTIONS[value]}</span>
              </span>
            </label>
          ))}
        </fieldset>

        {policy === 'allowlist' && (
          <div className="space-y-2" data-testid="connection-access-policy-allowlist">
            <div>
              <span className="block text-sm font-medium text-foreground">Allowed members</span>
              <p className="text-xs text-muted-foreground">
                The Owner is always present and cannot be removed. Members are authorized by stable Slack identity.
              </p>
            </div>
            <div className="flex flex-wrap gap-1.5" data-testid="connection-access-policy-chips">
              {ownerSlackUserId && (
                <span
                  className="inline-flex items-center gap-1 rounded-full border border-info/40 bg-info-subtle px-2 py-0.5 text-xs text-foreground"
                  data-testid="connection-access-policy-chip-owner"
                >
                  {ownerSlackUserId}
                  <span className="text-muted-foreground">(Owner)</span>
                </span>
              )}
              {members.map((id) => (
                <span
                  key={id}
                  className="inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 text-xs text-foreground"
                  data-testid="connection-access-policy-chip"
                  data-slack-user-id={id}
                >
                  <span className="truncate max-w-[12rem]">{id}</span>
                  <button
                    type="button"
                    className="-mr-1 text-muted-foreground hover:text-danger"
                    aria-label={`Remove ${id}`}
                    data-testid="connection-access-policy-chip-remove"
                    disabled={isSubmitting}
                    onClick={() => removeMember(id)}
                  >
                    ×
                  </button>
                </span>
              ))}
            </div>
            <div className="relative">
              <Input
                value={searchQuery}
                onChange={(e) => onSearchChange(e.target.value)}
                placeholder="Search workspace members by name or id…"
                aria-label="Search workspace members"
                className="text-sm"
                data-testid="connection-access-policy-member-search"
                disabled={isSubmitting}
                onFocus={() => searchResults.length > 0 && setSearchOpen(true)}
                onBlur={() => setTimeout(() => setSearchOpen(false), 150)}
              />
              {searchOpen && (
                <div
                  className="absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-md border border-border bg-popover text-sm shadow-md"
                  role="listbox"
                  data-testid="connection-access-policy-member-results"
                >
                  {isSearching && (
                    <div className="px-3 py-2 text-xs text-muted-foreground">Searching…</div>
                  )}
                  {!isSearching && searchResults.length === 0 && (
                    <div className="px-3 py-2 text-xs text-muted-foreground">No members found.</div>
                  )}
                  {!isSearching &&
                    searchResults.map((entry) => (
                      <button
                        key={entry.slackUserId}
                        type="button"
                        role="option"
                        aria-selected={members.includes(entry.slackUserId)}
                        className="flex w-full items-center justify-between gap-2 px-3 py-2 text-left hover:bg-accent"
                        data-testid="connection-access-policy-member-option"
                        data-slack-user-id={entry.slackUserId}
                        onMouseDown={(e) => {
                          e.preventDefault()
                          addMember(entry)
                        }}
                      >
                        <span className="min-w-0">
                          <span className="block truncate text-foreground">
                            {entry.displayName ?? entry.slackUserId}
                          </span>
                          <span className="block truncate text-xs text-muted-foreground">
                            {entry.slackUserId}
                          </span>
                        </span>
                      </button>
                    ))}
                </div>
              )}
            </div>
          </div>
        )}

        {policy === 'anyone' && (
          <div className="space-y-2 rounded-md border border-warning/40 bg-warning-subtle p-3" data-testid="connection-access-policy-anyone-disclosure">
            <p className="text-sm text-foreground">{anyoneDisclosure}</p>
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                checked={confirmedAnyone}
                onChange={(e) => setConfirmedAnyone(e.target.checked)}
                className="mt-0.5"
                data-testid="connection-access-policy-anyone-confirm"
                disabled={isSubmitting}
              />
              <span className="text-foreground">I understand and want to grant this authority to qualifying channel members.</span>
            </label>
          </div>
        )}

        {errorMessage && (
          <p className="text-xs text-danger" role="alert" data-testid="connection-access-policy-error">
            {errorMessage}
          </p>
        )}

        <Button type="submit" size="sm" disabled={!canSubmit} data-testid="connection-access-policy-submit">
          {isSubmitting ? <Loader2Icon className="size-4 animate-spin" /> : null}
          Save access policy
        </Button>
      </form>
    </CardSection>
  )
}
