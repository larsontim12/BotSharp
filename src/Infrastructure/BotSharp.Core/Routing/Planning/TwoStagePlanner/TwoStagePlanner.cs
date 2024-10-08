using BotSharp.Abstraction.Routing.Planning;

namespace BotSharp.Core.Routing.Planning;

public partial class TwoStagePlanner : IRoutingPlaner
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    public int MaxLoopCount => 100;
    private bool _isTaskCompleted;
    private string _md5;

    private Queue<FirstStagePlan> _plan1st = new Queue<FirstStagePlan>();
    private Queue<SecondStagePlan> _plan2nd = new Queue<SecondStagePlan>();

    private List<string> _executionContext = new List<string>();

    public TwoStagePlanner(IServiceProvider services, ILogger<TwoStagePlanner> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<FunctionCallFromLlm> GetNextInstruction(Agent router, string messageId, List<RoleDialogModel> dialogs)
    {
        if (_plan1st.IsNullOrEmpty() && _plan2nd.IsNullOrEmpty())
        {
            FirstStagePlan[] items = await GetFirstStagePlanAsync(router, messageId, dialogs);

            foreach (var item in items)
            {
                _plan1st.Enqueue(item);
            };
        }

        // Get Second Stage Plan
        if (_plan2nd.IsNullOrEmpty())
        {
            var plan1 = _plan1st.Dequeue();

            if (plan1.ContainMultipleSteps)
            {
                SecondStagePlan[] items = await GetSecondStagePlanAsync(router, messageId, plan1, dialogs);

                foreach (var item in items)
                {
                    _plan2nd.Enqueue(item);
                }
            }
            else
            {
                _plan2nd.Enqueue(new SecondStagePlan
                {
                    Description = plan1.Task,
                    Tables = plan1.Tables,
                    Parameters = plan1.Parameters,
                    Results = plan1.Results,
                });
            }
        }

        var plan2 = _plan2nd.Dequeue();

        var secondStagePrompt = GetSecondStageTaskPrompt(router, plan2);
        var inst = new FunctionCallFromLlm
        {
            AgentName = "SQL Driver",
            Response = secondStagePrompt,
            Function = "route_to_agent"
        };

        inst.HandleDialogsByPlanner = true;
        _isTaskCompleted = _plan1st.IsNullOrEmpty() && _plan2nd.IsNullOrEmpty();

        return inst;
    }

    public List<RoleDialogModel> BeforeHandleContext(FunctionCallFromLlm inst, RoleDialogModel message, List<RoleDialogModel> dialogs)
    {
        var question = inst.Response;
        if (_executionContext.Count > 0)
        {
            var content = GetContext();
            question = $"CONTEXT:\r\n{content}\r\n" + inst.Response;
        }
        else
        {
            question = $"CONTEXT:\r\n{question}";
        }

        var taskAgentDialogs = new List<RoleDialogModel>
        {
            new RoleDialogModel(AgentRole.User, question)
            {
                MessageId = message.MessageId,
            }
        };

        return taskAgentDialogs;
    }

    public bool AfterHandleContext(List<RoleDialogModel> dialogs, List<RoleDialogModel> taskAgentDialogs)
    {
        dialogs.AddRange(taskAgentDialogs.Skip(1));

        // Keep execution context
        _executionContext.Add(taskAgentDialogs.Last().Content);

        return true;
    }

    public async Task<bool> AgentExecuting(Agent router, FunctionCallFromLlm inst, RoleDialogModel message, List<RoleDialogModel> dialogs)
    {
        dialogs.Add(new RoleDialogModel(AgentRole.User, inst.Response)
        {
            MessageId = message.MessageId,
            CurrentAgentId = router.Id
        });
        return true;
    }

    public async Task<bool> AgentExecuted(Agent router, FunctionCallFromLlm inst, RoleDialogModel message, List<RoleDialogModel> dialogs)
    {
        var context = _services.GetRequiredService<IRoutingContext>();

        if (message.StopCompletion || _isTaskCompleted)
        {
            context.Empty(reason: $"Agent queue is cleared by {nameof(TwoStagePlanner)}");
            return false;
        }

        var routing = _services.GetRequiredService<IRoutingService>();
        routing.ResetRecursiveCounter();
        return true;
    }
}
