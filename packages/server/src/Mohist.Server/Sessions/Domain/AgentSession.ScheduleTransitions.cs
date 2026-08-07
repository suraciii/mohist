namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public SessionScheduleRecord CreateSchedule(
            string scheduleId,
            string text,
            DateTime dueAt,
            string idempotencyKey,
            DateTime now)
        {
            var schedule = new SessionScheduleRecord(
                scheduleId,
                dueAt,
                text,
                SessionScheduleStatus.Scheduled,
                idempotencyKey,
                now);
            var schedules = (session.Status.Schedules ?? []).ToList();
            schedules.Add(schedule);
            session.Status = session.Status with { Schedules = schedules };
            return schedule;
        }

        public SessionScheduleRecord? FindSchedule(string scheduleId) =>
            (session.Status.Schedules ?? []).FirstOrDefault(candidate =>
                string.Equals(candidate.ScheduleId, scheduleId, StringComparison.Ordinal));

        public SessionScheduleRecord? FindScheduleByIdempotencyKey(string idempotencyKey) =>
            (session.Status.Schedules ?? []).FirstOrDefault(candidate =>
                string.Equals(candidate.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));

        public IReadOnlyList<SessionScheduleRecord> SortedSchedules() =>
            (session.Status.Schedules ?? [])
                .OrderBy(candidate => candidate.DueAt)
                .ThenBy(candidate => candidate.ScheduleId, StringComparer.Ordinal)
                .ToArray();

        public bool HasNonTerminalSchedules() =>
            (session.Status.Schedules ?? []).Any(candidate => !candidate.IsTerminal);

        /// <summary>
        /// 到期后投递尝试开始：scheduled -> pending-delivery。
        /// </summary>
        public SessionScheduleRecord BeginScheduleDelivery(string scheduleId, DateTime now)
        {
            var index = FindScheduleIndex(session, scheduleId);
            var schedules = (session.Status.Schedules ?? []).ToList();
            var current = schedules[index];
            if (current.Status != SessionScheduleStatus.Scheduled)
                return current;
            var next = current with { Status = SessionScheduleStatus.PendingDelivery };
            schedules[index] = next;
            session.Status = session.Status with { Schedules = schedules };
            return next;
        }

        /// <summary>
        /// 投递受理成功：pending-delivery -> delivered，记录 InputId。
        /// </summary>
        public SessionScheduleRecord MarkScheduleDelivered(string scheduleId, string inputId)
        {
            var index = FindScheduleIndex(session, scheduleId);
            var schedules = (session.Status.Schedules ?? []).ToList();
            var current = schedules[index];
            if (current.Status == SessionScheduleStatus.Delivered)
                return current;
            var next = current with { Status = SessionScheduleStatus.Delivered, InputId = inputId };
            schedules[index] = next;
            session.Status = session.Status with { Schedules = schedules };
            return next;
        }

        /// <summary>
        /// 调用方显式取消：scheduled / pending-delivery -> cancelled。
        /// delivered / cancelled 是终态，返回当前记录不做转移。
        /// </summary>
        public SessionScheduleRecord CancelSchedule(string scheduleId, DateTime now)
        {
            var index = FindScheduleIndex(session, scheduleId);
            var schedules = (session.Status.Schedules ?? []).ToList();
            var current = schedules[index];
            if (current.IsTerminal)
                return current;
            var next = current with
            {
                Status = SessionScheduleStatus.Cancelled,
                CancelledAt = now,
            };
            schedules[index] = next;
            session.Status = session.Status with { Schedules = schedules };
            return next;
        }

        private static int FindScheduleIndex(AgentSession target, string scheduleId)
        {
            var schedules = target.Status.Schedules ?? [];
            for (var index = 0; index < schedules.Count; index++)
            {
                if (string.Equals(schedules[index].ScheduleId, scheduleId, StringComparison.Ordinal))
                    return index;
            }
            throw new ScheduleNotFoundException(target.Id, scheduleId);
        }
    }
}
