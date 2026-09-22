using System.Collections.Concurrent;
using NeuroCamera.Engine;
using Xunit;

namespace NeuroCamera.Tests;

public class LatestValueMailboxTests
{
    private sealed class Frame
    {
        public Frame(int id) => Id = id;

        public int Id { get; }
    }

    [Fact]
    public void First_post_asks_for_a_consumer_and_later_posts_do_not()
    {
        LatestValueMailbox<Frame> mailbox = new();

        Assert.True(mailbox.Post(new Frame(1)));
        Assert.False(mailbox.Post(new Frame(2)));
        Assert.False(mailbox.Post(new Frame(3)));
    }

    [Fact]
    public void Consumer_receives_only_the_newest_value()
    {
        LatestValueMailbox<Frame> mailbox = new();
        mailbox.Post(new Frame(1));
        mailbox.Post(new Frame(2));
        mailbox.Post(new Frame(3));

        Assert.True(mailbox.TryTake(out Frame? taken));
        Assert.NotNull(taken);
        Assert.Equal(3, taken!.Id);
        Assert.False(mailbox.TryTake(out Frame? nothing));
        Assert.Null(nothing);
    }

    [Fact]
    public void After_the_consumer_ran_the_next_post_asks_for_a_new_consumer()
    {
        LatestValueMailbox<Frame> mailbox = new();
        Assert.True(mailbox.Post(new Frame(1)));
        Assert.True(mailbox.TryTake(out _));

        Assert.True(mailbox.Post(new Frame(2)));
    }

    [Fact]
    public void Clear_discards_the_pending_value()
    {
        LatestValueMailbox<Frame> mailbox = new();
        mailbox.Post(new Frame(1));

        mailbox.Clear();

        Assert.False(mailbox.TryTake(out Frame? taken));
        Assert.Null(taken);
    }

    [Fact]
    public void Clearing_does_not_stop_the_next_post_from_being_delivered()
    {
        LatestValueMailbox<Frame> mailbox = new();
        Assert.True(mailbox.Post(new Frame(1)));
        mailbox.Clear();

        // The already scheduled consumer runs and finds nothing...
        Assert.False(mailbox.TryTake(out _));

        // ...and afterwards a fresh post schedules a fresh consumer.
        Assert.True(mailbox.Post(new Frame(2)));
        Assert.True(mailbox.TryTake(out Frame? taken));
        Assert.Equal(2, taken!.Id);
    }

    [Fact]
    public void The_last_posted_value_is_never_stranded_under_concurrent_posting_and_taking()
    {
        const int posts = 200_000;
        LatestValueMailbox<Frame> mailbox = new();
        BlockingCollection<bool> consumerQueue = new();
        int lastSeen = -1;
        int consumerRuns = 0;

        Thread consumer = new(() =>
        {
            foreach (bool _ in consumerQueue.GetConsumingEnumerable())
            {
                consumerRuns++;
                if (mailbox.TryTake(out Frame? frame))
                {
                    lastSeen = frame!.Id;
                }
            }
        })
        { IsBackground = true };
        consumer.Start();

        for (int id = 1; id <= posts; id++)
        {
            if (mailbox.Post(new Frame(id)))
            {
                consumerQueue.Add(true);
            }
        }

        consumerQueue.CompleteAdding();
        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(posts, lastSeen);
        Assert.True(consumerRuns >= 1);
        Assert.True(consumerRuns <= posts);
    }
}
