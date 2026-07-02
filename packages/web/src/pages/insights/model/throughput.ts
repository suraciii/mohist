import type { CompletionTotalDto, CompletionTrendResponse } from '../../../entities/issue'
import {
  type FullVerdict,
  type Verdict,
  directionForCounts,
  isFavorable,
} from './verdict'

/**
 * 产出节奏 verdict — issue-completion throughput.
 *
 * Sources the current and previous counts from the completion surface's
 * `currentTotal` / `previousTotal` returns. Each carries its own
 * `sampleCount` discriminator; the verdict surfaces the empty result
 * through the `insufficient` / `currentOnly` branches instead of
 * fabricating a value.
 *
 * Magnitude type: count delta (D6). Polarity: ↑ favorable.
 */
export interface ThroughputInputs {
  completion: CompletionTrendResponse | null | undefined
}

function emptyVerdict(): Verdict {
  return { kind: 'insufficient', label: '产出节奏' }
}

export function deriveThroughputVerdict(inputs: ThroughputInputs): Verdict {
  const current: CompletionTotalDto | undefined = inputs.completion?.currentTotal
  const previous: CompletionTotalDto | undefined = inputs.completion?.previousTotal

  if (!current || current.sampleCount === 0) {
    return emptyVerdict()
  }

  const direction = (() => {
    if (!previous || previous.sampleCount === 0) {
      return undefined
    }
    return directionForCounts(current.completed, previous.completed)
  })()

  if (direction === undefined) {
    return {
      kind: 'currentOnly',
      label: '产出节奏',
    }
  }

  const magnitude = current.completed - previous!.completed
  const full: FullVerdict = {
    kind: 'full',
    label: '产出节奏',
    direction,
    magnitude,
    unit: 'count',
    polarity: 'up-favorable',
  }
  return full
}

/**
 * Side-channel helper for tests/UI to read the favorable flag without
 * re-implementing the polarity rule.
 */
export function throughputIsFavorable(verdict: Verdict): boolean | null {
  if (verdict.kind !== 'full') return null
  return isFavorable(verdict.direction, verdict.polarity)
}