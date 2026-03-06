#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
namespace Foundatio.FastCloner.Code;

internal class AhoCorasick
{
    private class Node
    {
        public readonly Dictionary<char, Node> Children = new Dictionary<char, Node>();
        public Node? Failure;
        public bool IsEndOfPattern;
    }

    private readonly Node root = new Node();
    private readonly string[] patterns;

    public AhoCorasick(string[] patterns)
    {
        this.patterns = patterns;
        BuildTrie();
        BuildFailureLinks();
    }

    private void BuildTrie()
    {
        foreach (string pattern in patterns)
        {
            Node current = root;
            foreach (char c in pattern)
            {
                if (!current.Children.TryGetValue(c, out Node value))
                {
                    value = new Node();
                    current.Children[c] = value;
                }
                current = value;
            }
            
            current.IsEndOfPattern = true;
        }
    }

    private void BuildFailureLinks()
    {
        Queue<Node> queue = new Queue<Node>();
        
        foreach (Node node in root.Children.Values)
        {
            node.Failure = root;
            queue.Enqueue(node);
        }

        while (queue.Count > 0)
        {
            Node current = queue.Dequeue();
            
            foreach (KeyValuePair<char, Node> kvp in current.Children)
            {
                queue.Enqueue(kvp.Value);

                Node? failure = current.Failure;
                
                while (failure != null && !failure.Children.ContainsKey(kvp.Key))
                {
                    failure = failure.Failure;
                }

                kvp.Value.Failure = failure?.Children.GetValueOrDefault(kvp.Key) ?? root;
            }
        }
    }

    public bool ContainsAnyPattern(string text)
    {
        Node? current = root;
        
        foreach (char c in text)
        {
            while (current != null && !current.Children.ContainsKey(c))
            {
                current = current.Failure;
            }
            
            current = current?.Children.GetValueOrDefault(c) ?? root;

            if (current.IsEndOfPattern)
            {
                return true;
            }
        }
        return false;
    }
}
