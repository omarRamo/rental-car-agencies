using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class EntityTests
{
    [Fact]
    public void Entities_with_the_same_type_and_id_are_equal()
    {
        var id = Entity.NewId();

        var first = new SampleAggregate(id);
        var second = new SampleAggregate(id);

        (first == second).ShouldBeTrue();
        first.Equals((object)second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        (first != new SampleAggregate(Entity.NewId())).ShouldBeTrue();
        first.Equals(null).ShouldBeFalse();
        first.Equals(first).ShouldBeTrue();
    }

    [Fact]
    public void Entities_of_different_types_are_never_equal()
    {
        var id = Entity.NewId();

        new SampleAggregate(id).Equals(new OtherAggregate(id)).ShouldBeFalse();
    }

    [Fact]
    public void Empty_ids_are_rejected()
    {
        Should.Throw<ArgumentException>(() => new SampleAggregate(Guid.Empty));
    }

    [Fact]
    public void NewId_is_a_version_7_guid()
    {
        var id = Entity.NewId();

        id.Version.ShouldBe(7);
    }

    [Fact]
    public void Aggregates_collect_and_dequeue_domain_events()
    {
        var aggregate = new SampleAggregate(Entity.NewId());

        aggregate.DoSomething();
        aggregate.DoSomething();

        aggregate.DomainEvents.Count.ShouldBe(2);
        var events = aggregate.DequeueDomainEvents();
        events.Count.ShouldBe(2);
        events[0].ShouldBeOfType<SomethingHappened>().AggregateId.ShouldBe(aggregate.Id);
        aggregate.DomainEvents.ShouldBeEmpty();
        aggregate.DequeueDomainEvents().ShouldBeEmpty();
    }

    [Fact]
    public void Raising_a_null_event_is_a_bug()
    {
        Should.Throw<ArgumentNullException>(() => new SampleAggregate(Entity.NewId()).RaiseNull());
    }

    private sealed record SomethingHappened(Guid AggregateId) : IDomainEvent;

    private sealed class SampleAggregate(Guid id) : AggregateRoot(id)
    {
        public void DoSomething() => Raise(new SomethingHappened(Id));

        public void RaiseNull() => Raise(null!);
    }

    private sealed class OtherAggregate(Guid id) : AggregateRoot(id);
}
