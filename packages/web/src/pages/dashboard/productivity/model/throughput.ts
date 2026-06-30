export function computeMovingAverage(values: number[], window: number): number[] {
  return values.map((_, i) => {
    const start = Math.max(0, i - window + 1)
    const count = i - start + 1
    let sum = 0
    for (let j = start; j <= i; j++) {
      sum += values[j]
    }
    return sum / count
  })
}
